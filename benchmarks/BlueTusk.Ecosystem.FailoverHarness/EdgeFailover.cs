using System.Data.Common;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using BlueTusk.Data;
using BlueTusk.Edge;
using BlueTusk.Edge.AspNetCore;
using BlueTusk.Edge.Http;
using BlueTusk.Edge.Server;
using BlueTusk.Edge.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlueTusk.Ecosystem.FailoverHarness;

/// <summary>
/// Edge delivers client mutations at least once over HTTP; caller-stable mutation identities make
/// the server apply each one, and run its business effect, exactly once, and an ordered stream
/// refuses (410) a retry at or below its admitted sequence (docs/edge/README.md). Each disturbance
/// interrupts the server while it applies an ordered mutation, after the business effect is
/// staged in the same transaction. Recovery must roll that apply back whole, then the client's
/// retained outbox must deliver it exactly once, keep every acknowledged effect singular, keep
/// tenants isolated and leave each client checkpoint at the server head. The host-process kill
/// kills the Edge server process; the SQLite clients survive, as on a real device.
/// </summary>
internal sealed class EdgeFailover : FailoverCase
{
    private const int MutationsPerTenant = 8;
    private const string InFlightId = "inflight";
    private static readonly string[] TenantIds = ["tenant-a", "tenant-b"];
    private readonly FailoverFixture _fixture;
    private readonly string _schema;
    private readonly BlueTuskDataSource _source;
    private readonly PostgreSqlEdgeServerStore _store;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bluetusk-failover-edge-" + Guid.NewGuid().ToString("N"));
    private readonly Client[] _clients = new Client[2];
    private EdgeHost? _host;
    private Uri? _endpoint;
    private Task? _inFlight;
    private EdgeMutation? _first;
    private long _headBefore;

    private EdgeFailover(FailoverFixture fixture, string application, string schema)
    {
        _fixture = fixture;
        _schema = schema;
        _source = fixture.CreateSource(application, 6);
        _store = new PostgreSqlEdgeServerStore(_source, new EdgeServerOptions { Schema = schema });
    }

    internal static Task<IReadOnlyList<ScenarioResult>> RunAsync(FailoverFixture fixture, CancellationToken token) =>
        ScenarioDriver.RunAllAsync(fixture, () => new EdgeFailover(fixture, ScenarioDriver.Application, "fo_edge_" + Guid.NewGuid().ToString("N")[..24]),
            [FaultKind.BackendTermination, FaultKind.HostProcessKill, FaultKind.PrimaryCrashRestart, FaultKind.StandbyPromotion], token);

    internal override string Family => "Edge";
    internal override string Semantics => "at-least-once-delivery; exactly-once-apply-by-mutation-identity; ordered-retry-fence; client-outbox-retained";
    internal override int Tenants => TenantIds.Length;
    internal override long BarrierKey => 180942166501;
    internal override string[] ChildArguments => [_schema];

    internal override async Task PrepareAsync(CancellationToken token)
    {
        await _store.InitializeAsync(token);
        foreach (var scope in Scopes) { await _store.ActivateScopeAsync(scope, token); }
        await FailoverFixture.ExecuteAsync(_fixture.Admin,
            $"CREATE TABLE \"{_schema}\".business_effects(tenant text NOT NULL, document_id text NOT NULL, counter integer NOT NULL, PRIMARY KEY(tenant, document_id))", token);
        Directory.CreateDirectory(_directory);
        for (int tenant = 0; tenant < TenantIds.Length; tenant++)
        {
            var local = new SqliteEdgeStore(new SqliteEdgeOptions { DatabasePath = Path.Combine(_directory, TenantIds[tenant] + ".db") });
            await local.InitializeAsync(token);
            await local.ActivateScopeAsync(Scopes[tenant], cancellationToken: token);
            _clients[tenant] = new Client(local, Http(TenantIds[tenant]));
        }

        if (Fault != FaultKind.HostProcessKill)
        {
            _host = await EdgeHost.StartAsync(_store, _schema, BarrierKey, token);
            _endpoint = _host.Endpoint;
        }
    }

    internal override async Task<int> AcknowledgeAsync(CancellationToken token)
    {
        int acknowledged = 0;
        for (int tenant = 0; tenant < TenantIds.Length; tenant++)
        {
            await SynchronizeAsync(tenant, token);
            for (int index = 0; index < MutationsPerTenant; index++)
            {
                var mutation = await _clients[tenant].Local.EnqueueOrderedAsync(Scopes[tenant], Id(index), 0, EdgeMutationKind.Upsert, Payload(tenant, Id(index)), token);
                _first ??= mutation;
                await SynchronizeAsync(tenant, token);
                FailoverFixture.Check((await _clients[tenant].Local.GetAsync(Scopes[tenant], Id(index), token))!.PendingMutationId is null, "the server acknowledged the mutation");
                acknowledged++;
            }
        }

        _headBefore = await HeadAsync(0, token);
        return acknowledged;
    }

    internal override Task StartInFlightAsync(CancellationToken token)
    {
        _inFlight = StartInFlightSyncAsync(token);
        return _inFlight;
    }

    internal override async Task<int> ReadChildAcknowledgementsAsync(ChildProcess child, CancellationToken token)
    {
        string[] line = (await child.ReadLineAsync(ScenarioDriver.Phase, token)).Split(' ');
        FailoverFixture.Check(line is ["ENDPOINT", _] && Uri.TryCreate(line[1], UriKind.Absolute, out _), "the child server reported its endpoint");
        _endpoint = new Uri(line[1]);
        return await AcknowledgeAsync(token);
    }

    internal override Task StartChildInFlightAsync(ChildProcess child, CancellationToken token)
    {
        _inFlight = StartInFlightSyncAsync(token);
        return Task.CompletedTask;
    }

    internal override async Task CheckRolledBackAsync(ScenarioRecorder recorder, CancellationToken token)
    {
        recorder.Check(await ScenarioDriver.FailedAsync(_inFlight!), "interrupted synchronization reported failure to the client");
        bool effect = await EffectAsync(0, InFlightId, token) != 0;
        bool record = await _store.GetAsync(Scopes[0], InFlightId, token) is not null;
        bool pending = (await _clients[0].Local.GetAsync(Scopes[0], InFlightId, token))!.PendingMutationId is not null;
        recorder.InFlightAtomic = effect == record && !effect && pending;
        recorder.Check(recorder.InFlightAtomic, "interrupted apply rolled back whole and the client outbox retained the mutation");
    }

    internal override async Task RecoverAsync(CancellationToken token)
    {
        if (_host is null || _host.Endpoint != _endpoint)
        {
            // The killed server process is replaced; the client keeps its local outbox and lease.
            _host ??= await EdgeHost.StartAsync(_store, _schema, BarrierKey, token);
            _endpoint = _host.Endpoint;
        }

        await SynchronizeAsync(0, token);
        FailoverFixture.Check((await _clients[0].Local.GetAsync(Scopes[0], InFlightId, token))!.PendingMutationId is null, "retained mutation acknowledged");
    }

    internal override async Task VerifyAsync(ScenarioRecorder recorder, CancellationToken token)
    {
        await SynchronizeAsync(1, token);
        int verified = 0;
        bool isolated = true;
        for (int tenant = 0; tenant < TenantIds.Length; tenant++)
        {
            for (int index = 0; index < MutationsPerTenant; index++)
            {
                var server = await _store.GetAsync(Scopes[tenant], Id(index), token);
                var local = await _clients[tenant].Local.GetAsync(Scopes[tenant], Id(index), token);
                if (server is not null && Encoding.UTF8.GetString(server.Payload.Span) == Encoding.UTF8.GetString(Payload(tenant, Id(index)).Span) &&
                    local is { PendingMutationId: null } && local.ServerRevision == server.Revision && await EffectAsync(tenant, Id(index), token) == 1)
                {
                    verified++;
                }
            }

            recorder.Check((await _clients[tenant].Local.GetCheckpointAsync(Scopes[tenant], token)).Position == await HeadAsync(tenant, token), "every client checkpoint reached the server head");
            // Each tenant may read only its own scope, and the same document identity keeps its own tenant payload.
            using var foreign = new HttpEdgeRemoteTransport(_clients[tenant].Http, _endpoint!);
            try
            {
                _ = await foreign.BeginSnapshotAsync(Scopes[1 - tenant], token);
                isolated = false;
            }
            catch (EdgeHttpTransportException exception) when (exception.StatusCode == 403) { }
        }

        recorder.Verified = verified;
        recorder.Check(verified == MutationsPerTenant * TenantIds.Length, "every acknowledged mutation survived with one business effect and a converged client cache");
        recorder.NoCrossTenantReads = isolated;
        recorder.Check(isolated, "no cross-tenant reads");
        recorder.InFlightCommitted = await EffectAsync(0, InFlightId, token) == 1 && await _store.GetAsync(Scopes[0], InFlightId, token) is not null;
        recorder.Check(recorder.InFlightCommitted, "the retained mutation was applied exactly once after recovery");
        recorder.BeforeFence = _headBefore;
        recorder.AfterFence = await HeadAsync(0, token);
        recorder.Check(recorder.AfterFence == recorder.BeforeFence + 1, "the server feed advanced by exactly the recovered mutation");
        using (var stale = new HttpEdgeRemoteTransport(_clients[0].Http, _endpoint!))
        {
            try
            {
                _ = await stale.ApplyMutationAsync(_first!, token);
            }
            catch (EdgeHttpTransportException exception) when (exception.StatusCode == 410)
            {
                recorder.StaleOwnerRejected = true;
            }
        }

        recorder.Check(recorder.StaleOwnerRejected, "a confirmed ordered retry is refused");
        recorder.ExpectedEffects = MutationsPerTenant * TenantIds.Length + 1;
        recorder.ObservedEffects = await FailoverFixture.ScalarAsync<long>(_fixture.Admin, $"SELECT COALESCE(sum(counter), 0) FROM \"{_schema}\".business_effects", token);
        recorder.Check(recorder.ObservedEffects == recorder.ExpectedEffects, "exactly one business effect per acknowledged mutation");
    }

    internal static async Task RunChildAsync(string role, string[] arguments, CancellationToken token)
    {
        FailoverFixture.Check(role == "workload" && arguments is [var schema] && schema.Length == "fo_edge_".Length + 24 &&
            schema.StartsWith("fo_edge_", StringComparison.Ordinal) && schema["fo_edge_".Length..].All(char.IsAsciiHexDigitLower), "parent-generated Edge schema");
        await using var fixture = new FailoverFixture();
        await using var source = fixture.CreateSource(ScenarioDriver.ChildApplication, 6);
        await using var store = new PostgreSqlEdgeServerStore(source, new EdgeServerOptions { Schema = arguments[0] });
        await using var host = await EdgeHost.StartAsync(store, arguments[0], 180942166501, token);
        Console.WriteLine("ENDPOINT " + host.Endpoint);
        await Console.Out.FlushAsync(token);
        await Task.Delay(Timeout.Infinite, token);
    }

    private async Task StartInFlightSyncAsync(CancellationToken token)
    {
        _ = await _clients[0].Local.EnqueueOrderedAsync(Scopes[0], InFlightId, 0, EdgeMutationKind.Upsert, Payload(0, InFlightId), token);
        await SynchronizeAsync(0, token);
    }

    private async Task SynchronizeAsync(int tenant, CancellationToken token)
    {
        using var transport = new HttpEdgeRemoteTransport(_clients[tenant].Http, _endpoint!);
        await new EdgeSynchronizationCoordinator(_clients[tenant].Local, transport).SynchronizeAsync(Scopes[tenant], cancellationToken: token);
    }

    private async Task<long> HeadAsync(int tenant, CancellationToken token) =>
        (await _store.ReadChangesAsync(Scopes[tenant], 0, 512, token))?.ToPosition ?? 0;

    private async Task<long> EffectAsync(int tenant, string id, CancellationToken token) =>
        await FailoverFixture.ScalarAsync<long>(_fixture.Admin,
            $"SELECT COALESCE(sum(counter), 0) FROM \"{_schema}\".business_effects WHERE tenant='{TenantIds[tenant]}' AND document_id='{id}'", token);

    private static readonly EdgeScope[] Scopes = [new(TenantIds[0], "orders", 1), new(TenantIds[1], "orders", 1)];
    private static string Id(int index) => "doc-" + index.ToString("D2", CultureInfo.InvariantCulture);
    private static ReadOnlyMemory<byte> Payload(int tenant, string id) => Encoding.UTF8.GetBytes("{\"tenant\":\"" + TenantIds[tenant] + "\",\"id\":\"" + id + "\"}");

    private static HttpClient Http(string tenant)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tenant);
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_host is not null) { await _host.DisposeAsync(); }
        foreach (var client in _clients)
        {
            if (client is null) { continue; }
            client.Http.Dispose();
        }

        await _store.DisposeAsync();
        await _source.DisposeAsync();
        string resolved = Path.GetFullPath(_directory);
        if (Directory.Exists(resolved) && resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(resolved).StartsWith("bluetusk-failover-edge-", StringComparison.Ordinal))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(resolved, true);
        }
    }

    private sealed record Client(SqliteEdgeStore Local, HttpClient Http);

    /// <summary>The documented ASP.NET Edge endpoints with a business effect in the apply transaction.</summary>
    private sealed class EdgeHost(WebApplication application, Uri endpoint) : IAsyncDisposable
    {
        internal Uri Endpoint { get; } = endpoint;

        internal static async Task<EdgeHost> StartAsync(PostgreSqlEdgeServerStore store, string schema, long barrierKey, CancellationToken token)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddAuthentication("failover").AddScheme<AuthenticationSchemeOptions, TenantAuthentication>("failover", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy("edge", policy => policy.AddAuthenticationSchemes("failover").RequireAuthenticatedUser()));
            var application = builder.Build();
            application.UseAuthentication();
            application.UseAuthorization();
            application.MapBlueTuskEdge(store, static (context, scope, _) => ValueTask.FromResult(
                context.User.FindFirstValue("tenant") == scope.Tenant && scope.Id == "orders" && scope.Epoch == 1), new EdgeEndpointOptions
                {
                    AuthorizationPolicy = "edge",
                    WriteBusinessAsync = async (connection, transaction, mutation, record, cancellationToken) =>
                    {
                        FailoverFixture.Check(record.Id == mutation.DocumentId, "business record identity");
                        await ExecuteAsync(connection, transaction,
                            $"INSERT INTO \"{schema}\".business_effects VALUES(@tenant,@id,1) ON CONFLICT(tenant, document_id) DO UPDATE SET counter=business_effects.counter+1",
                            mutation.Scope.Tenant, mutation.DocumentId, cancellationToken);
                        // The in-flight mutation blocks after its business effect is staged in the apply transaction.
                        if (mutation.DocumentId == InFlightId)
                        {
                            await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(" + barrierKey.ToString(CultureInfo.InvariantCulture) + ")", null, null, cancellationToken);
                        }
                    },
                });
            await application.StartAsync(token);
            return new(application, new Uri(application.Urls.Single().TrimEnd('/') + "/edge"));
        }

        private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, string? tenant, string? id, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            if (tenant is not null)
            {
                var first = command.CreateParameter(); first.ParameterName = "tenant"; first.Value = tenant; command.Parameters.Add(first);
                var second = command.CreateParameter(); second.ParameterName = "id"; second.Value = id; command.Parameters.Add(second);
            }

            _ = await command.ExecuteNonQueryAsync(token);
        }

        public async ValueTask DisposeAsync()
        {
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }

    private sealed class TenantAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? header = Request.Headers.Authorization;
            string? tenant = header is { } value && value.StartsWith("Bearer ", StringComparison.Ordinal) ? value["Bearer ".Length..] : null;
            if (tenant is not ("tenant-a" or "tenant-b")) { return Task.FromResult(AuthenticateResult.NoResult()); }
            var identity = new ClaimsIdentity([new Claim("tenant", tenant)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}

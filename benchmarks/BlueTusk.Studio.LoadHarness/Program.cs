using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlueTusk.Benchmarks.ExpansionCapacity;
using BlueTusk.Data;
using BlueTusk.Events;
using BlueTusk.Schema;
using BlueTusk.Studio.Events;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlueTusk.Studio.LoadHarness;

internal static class Program
{
    private static Task<int> Main(string[] args) =>
        CapacityHost.RunAsync(args, "Studio", static (context, cancellationToken) => StudioWorkload.CreateAsync(context, cancellationToken));
}

internal sealed record StudioQueryBody(string Sql, Guid OperationId, bool Explain = false, int? MaximumRows = null);

// Sustained Studio hot paths over real Kestrel HTTP with fixture authentication, antiforgery and
// the durable PostgreSqlStudioAuditSink: audited read-only queries, explains, schema reads and
// event paging. Every response is checked against the seeded fixture.
internal sealed class StudioWorkload : CapacityWorkload
{
    private const string UserHeader = "X-Capacity-User";
    private readonly BlueTuskDataSource _source;
    private readonly string _application;
    private readonly string _audit;
    private readonly PostgreSqlEventsOptions _eventsOptions;
    private readonly int _tenants;
    private readonly int _orders;
    private readonly int _tables;
    private readonly int _queryRows;
    private readonly int _streams;
    private readonly int _eventsPerStream;
    private readonly int _eventPayloadBytes;
    private readonly int _pageSize;
    private readonly int _queryWorkers;
    private readonly int _pageWorkers;
    private WebApplication? _host;
    private HttpClient? _client;
    private HttpClientHandler? _handler;
    private Uri? _baseAddress;
    private string? _schemaFingerprint;
    private long _acceptedAudited;
    private long _verifiedSchemaReads;

    private StudioWorkload(CapacityContext context, BlueTuskDataSource source)
    {
        _source = source;
        var prefix = "studio_capacity_" + Guid.NewGuid().ToString("N")[..12];
        _application = prefix + "_app";
        _audit = prefix + "_audit";
        _eventsOptions = new PostgreSqlEventsOptions { Schema = prefix + "_events" };
        _tenants = context.IntParameter("tenants");
        _orders = context.IntParameter("ordersPerTenant");
        _tables = context.IntParameter("applicationTables");
        _queryRows = context.IntParameter("queryRows");
        _streams = context.IntParameter("eventStreams");
        _eventsPerStream = context.IntParameter("eventsPerStream");
        _eventPayloadBytes = context.IntParameter("eventPayloadBytes");
        _pageSize = context.IntParameter("eventPageSize");
        _queryWorkers = context.Schedule("query").Workers;
        _pageWorkers = context.Schedule("event-page").Workers;
        CapacityHost.Check(_eventsPerStream % _pageSize == 0, "Studio event streams must hold whole pages.");
    }

    private HttpClient Client => _client ?? throw new InvalidOperationException("The Studio client is not started.");

    public static async Task<CapacityWorkload> CreateAsync(CapacityContext context, CancellationToken cancellationToken)
    {
        var settings = new BlueTuskConnectionStringBuilder(context.ConnectionString)
        {
            MaximumPoolSize = context.IntParameter("maximumPoolSize"),
            ApplicationName = "bluetusk-studio-capacity",
        };
        var workload = new StudioWorkload(context, BlueTuskDataSource.Create(settings.ConnectionString));
        try
        {
            await workload.SeedAsync(cancellationToken);
            await workload.StartHostAsync(context, cancellationToken);
            return workload;
        }
        catch
        {
            await workload.DisposeAsync();
            throw;
        }
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        var ddl = new System.Text.StringBuilder();
        ddl.Append(CultureInfo.InvariantCulture, $"""
            CREATE SCHEMA "{_application}";
            CREATE TABLE "{_application}".orders (
                id bigint PRIMARY KEY, tenant integer NOT NULL, status text NOT NULL, amount numeric(12,2) NOT NULL, note text NOT NULL);
            CREATE INDEX orders_tenant_id ON "{_application}".orders (tenant, id);
            INSERT INTO "{_application}".orders
                SELECT g, g % {_tenants}, (ARRAY['new','paid','shipped','closed'])[g % 4 + 1], (g % 1000) + 0.5, md5(g::text)
                FROM generate_series(1, {_tenants * _orders}) AS g;
            """);
        for (var table = 1; table < _tables; table++)
        {
            ddl.Append(CultureInfo.InvariantCulture, $"""

                CREATE TABLE "{_application}".reference_{table:D2} (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, order_id bigint NOT NULL REFERENCES "{_application}".orders(id),
                    label text NOT NULL CHECK (length(label) <= 128), created_at timestamptz NOT NULL DEFAULT now());
                CREATE INDEX reference_{table:D2}_order ON "{_application}".reference_{table:D2} (order_id);
                """);
        }

        ddl.Append(CultureInfo.InvariantCulture, $"\nANALYZE \"{_application}\".orders;");
        await using (var command = _source.CreateCommand(ddl.ToString()))
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var audit = new PostgreSqlStudioAuditSink(_source, _audit);
        await audit.InitializeAsync(cancellationToken);
        var store = new PostgreSqlEventStore(_source, _eventsOptions);
        await store.InitializeAsync(cancellationToken);
        var payload = new byte[_eventPayloadBytes];
        for (var stream = 0; stream < _streams; stream++)
        {
            for (var first = 0; first < _eventsPerStream; first += 256)
            {
                var batch = new EventWrite[Math.Min(256, _eventsPerStream - first)];
                for (var item = 0; item < batch.Length; item++)
                {
#pragma warning disable CA5394 // Seeded high-entropy payloads are fixture content, not security material.
                    new Random(unchecked(stream * 1_000_003 + first + item)).NextBytes(payload);
#pragma warning restore CA5394
                    batch[item] = new EventWrite(Guid.CreateVersion7(), "capacity.trace", 1, DateTimeOffset.UnixEpoch, payload);
                }

                await using var connection = await _source.OpenConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                _ = await store.AppendAsync(connection, transaction, StreamKey(stream), batch, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }

        _schemaFingerprint = (await new PostgreSqlSchemaCapture(_source, new SchemaCaptureOptions { Schemas = [_application] })
            .CaptureAsync(cancellationToken)).Fingerprint;
    }

    private static EventStreamKey StreamKey(int stream) => new("capacity-tenant", $"trace-{stream:D2}");

    private async Task StartHostAsync(CapacityContext context, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var audit = new PostgreSqlStudioAuditSink(_source, _audit);
        var store = new PostgreSqlEventStore(_source, _eventsOptions);
        var streams = Enumerable.Range(0, _streams).ToDictionary(stream => $"s{stream}", StreamKey, StringComparer.Ordinal);
        builder.Services.AddSingleton(audit);
        builder.Services.AddSingleton(new CapacityScope(new StudioDatabaseScope(_source, [_application]) { AuditScopeId = "capacity-database" }));
        // AddBlueTuskStudioEvents constructs the resolver per request; it receives this fixed scope.
        builder.Services.AddSingleton(new StudioEventScope(store, streams));
        builder.Services.AddAuthentication("capacity").AddScheme<AuthenticationSchemeOptions, CapacityAuthentication>("capacity", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("studio-read", policy => policy.RequireAuthenticatedUser());
            options.AddPolicy("studio-query", policy => policy.RequireClaim("query", "allowed"));
        });
        builder.Services.AddBlueTuskStudio<CapacityScope, PostgreSqlStudioAuditSink>(new StudioOptions
        {
            ReadPolicy = "studio-read",
            QueryPolicy = "studio-query",
            MaximumConcurrentQueries = context.IntParameter("maximumConcurrentQueries"),
        });
        builder.Services.AddBlueTuskStudioEvents<CapacityEventScope>(new StudioEventOptions
        {
            MaximumEvents = _pageSize,
            MaximumConcurrentReads = context.IntParameter("maximumConcurrentEventReads"),
        });
        var host = builder.Build();
        host.UseAuthentication();
        host.UseAuthorization();
        host.MapBlueTuskStudio();
        host.MapBlueTuskStudioEvents();
        await host.StartAsync(cancellationToken);
        _host = host;
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _baseAddress = new Uri(address + "/bluetusk/studio/");
        (_handler, _client) = await SessionAsync("operator", withToken: true, cancellationToken);
    }

    private async Task<(HttpClientHandler Handler, HttpClient Client)> SessionAsync(string user, bool withToken, CancellationToken cancellationToken)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false, MaxConnectionsPerServer = 64 };
        var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = _baseAddress, Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add(UserHeader, user);
        using var session = JsonDocument.Parse(await client.GetStringAsync("session", cancellationToken));
        if (withToken)
        {
            client.DefaultRequestHeaders.Add(session.RootElement.GetProperty("header").GetString()!, session.RootElement.GetProperty("token").GetString());
        }

        return (handler, client);
    }

    public override IReadOnlyList<string> OwnedSchemas => [_application, _audit, _eventsOptions.Schema];

    public override IReadOnlyList<ScheduledOperation> Operations =>
    [
        new("query", QueryAsync),
        new("explain", ExplainAsync),
        new("schema", SchemaAsync),
        new("event-page", EventPageAsync),
    ];

    private string OrdersSql(int tenant) => string.Create(CultureInfo.InvariantCulture,
        $"SELECT id, tenant, status, amount FROM \"{_application}\".orders WHERE tenant = {tenant} ORDER BY id LIMIT {_queryRows}");

    private static void Expect(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"Studio returned {(int)response.StatusCode}.", null, response.StatusCode);
        }
    }

    private async ValueTask<OperationOutcome> QueryAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var tenant = (int)((slot * _queryWorkers + worker) % _tenants);
        using var response = await Client.PostAsJsonAsync("query", new StudioQueryBody(OrdersSql(tenant), Guid.CreateVersion7()), cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) { return OperationOutcome.Rejected; }
        Expect(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        var rows = body.RootElement.GetProperty("rows");
        CapacityHost.Check(body.RootElement.GetProperty("count").GetInt32() == _queryRows && rows.GetArrayLength() == _queryRows &&
            rows.EnumerateArray().All(row => row[1].GetInt32() == tenant), "A Studio query returned a foreign or truncated result.");
        Interlocked.Increment(ref _acceptedAudited);
        return OperationOutcome.Accepted;
    }

    private async ValueTask<OperationOutcome> ExplainAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var tenant = (int)(slot % _tenants);
        using var response = await Client.PostAsJsonAsync("query", new StudioQueryBody(OrdersSql(tenant), Guid.CreateVersion7(), Explain: true), cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) { return OperationOutcome.Rejected; }
        Expect(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        CapacityHost.Check(body.RootElement.GetProperty("count").GetInt32() >= 1, "A Studio explain returned no plan.");
        Interlocked.Increment(ref _acceptedAudited);
        return OperationOutcome.Accepted;
    }

    private async ValueTask<OperationOutcome> SchemaAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync("schema", cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) { return OperationOutcome.Rejected; }
        Expect(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        CapacityHost.Check(string.Equals(body.RootElement.GetProperty("Fingerprint").GetString(), _schemaFingerprint, StringComparison.Ordinal) &&
            body.RootElement.GetProperty("Relations").GetArrayLength() >= _tables, "A Studio schema read differs from the captured application schema.");
        Interlocked.Increment(ref _verifiedSchemaReads);
        return OperationOutcome.Accepted;
    }

    private async ValueTask<OperationOutcome> EventPageAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var ordinal = slot * _pageWorkers + worker;
        var stream = (int)(ordinal % _streams);
        var after = ordinal * 7 % (_eventsPerStream / _pageSize) * _pageSize;
        using var response = await Client.GetAsync(string.Create(CultureInfo.InvariantCulture,
            $"events/?stream=s{stream}&after={after}&limit={_pageSize}"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) { return OperationOutcome.Rejected; }
        Expect(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        var events = body.RootElement.GetProperty("events");
        var index = 0;
        foreach (var value in events.EnumerateArray())
        {
            CapacityHost.Check(value.GetProperty("sequence").GetString() == (after + ++index).ToString(CultureInfo.InvariantCulture) &&
                value.GetProperty("payloadBytes").GetInt32() == _eventPayloadBytes, "A Studio event page returned a gap or a foreign event.");
        }

        CapacityHost.Check(index == _pageSize, "A Studio event page was truncated.");
        Interlocked.Increment(ref _acceptedAudited);
        return OperationOutcome.Accepted;
    }

    private async Task<long> ScalarAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = _source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public override async Task DrainAndVerifyAsync(CapacityRecorder recorder, CancellationToken cancellationToken)
    {
        var expectedAudit = Interlocked.Read(ref _acceptedAudited) * 2;
        var auditRows = await ScalarAsync($"SELECT count(*) FROM \"{_audit}\".studio_audit", cancellationToken);
        var completed = await ScalarAsync($"SELECT count(*) FROM \"{_audit}\".studio_audit WHERE outcome IN ('completed', 'event-trace-completed')", cancellationToken);
        recorder.Counter("auditRows", auditRows);
        recorder.Counter("expectedAuditRows", expectedAudit);
        recorder.Counter("verifiedSchemaReads", Interlocked.Read(ref _verifiedSchemaReads));
        recorder.Invariant("audit-rows-exact", auditRows == expectedAudit && completed * 2 == expectedAudit,
            $"auditRows={auditRows}, completed={completed}, expected={expectedAudit}");
        recorder.Invariant("schema-fingerprint-stable", Interlocked.Read(ref _verifiedSchemaReads) > 0,
            $"Every one of {Interlocked.Read(ref _verifiedSchemaReads)} accepted schema reads matched the captured fingerprint.");

        var orders = await ScalarAsync($"SELECT count(*) FROM \"{_application}\".orders", cancellationToken);
        using (var write = await Client.PostAsJsonAsync("query", new StudioQueryBody(
            $"WITH removed AS (DELETE FROM \"{_application}\".orders RETURNING id) SELECT id FROM removed", Guid.CreateVersion7()), cancellationToken))
        using (var statement = await Client.PostAsJsonAsync("query", new StudioQueryBody(
            $"DELETE FROM \"{_application}\".orders", Guid.CreateVersion7()), cancellationToken))
        {
            var remaining = await ScalarAsync($"SELECT count(*) FROM \"{_application}\".orders", cancellationToken);
            recorder.Invariant("read-only-enforced", write.StatusCode != HttpStatusCode.OK && statement.StatusCode != HttpStatusCode.OK && remaining == orders,
                $"dataModifyingCte={(int)write.StatusCode}, delete={(int)statement.StatusCode}, rows={remaining}/{orders}");
        }

        using (var multiple = await Client.PostAsJsonAsync("query", new StudioQueryBody("SELECT 1; SELECT 2", Guid.CreateVersion7()), cancellationToken))
        {
            recorder.Invariant("multiple-statements-refused", multiple.StatusCode == HttpStatusCode.BadRequest, $"status={(int)multiple.StatusCode}");
        }

        using (var limited = await Client.PostAsJsonAsync("query", new StudioQueryBody(OrdersSql(0), Guid.CreateVersion7(), MaximumRows: 5), cancellationToken))
        {
            var count = -1;
            if (limited.StatusCode == HttpStatusCode.OK)
            {
                using var body = JsonDocument.Parse(await limited.Content.ReadAsByteArrayAsync(cancellationToken));
                count = body.RootElement.GetProperty("count").GetInt32();
            }

            recorder.Invariant("row-limit-enforced", count == 5, $"status={(int)limited.StatusCode}, count={count}");
        }

        var (tokenlessHandler, tokenless) = await SessionAsync("operator", withToken: false, cancellationToken);
        using (tokenlessHandler)
        using (tokenless)
        using (var forged = await tokenless.PostAsJsonAsync("query", new StudioQueryBody(OrdersSql(0), Guid.CreateVersion7()), cancellationToken))
        {
            recorder.Invariant("antiforgery-enforced", forged.StatusCode == HttpStatusCode.BadRequest, $"status={(int)forged.StatusCode}");
        }

        var (readerHandler, reader) = await SessionAsync("reader", withToken: true, cancellationToken);
        using (readerHandler)
        using (reader)
        using (var denied = await reader.PostAsJsonAsync("query", new StudioQueryBody(OrdersSql(0), Guid.CreateVersion7()), cancellationToken))
        {
            recorder.Invariant("query-policy-enforced", denied.StatusCode == HttpStatusCode.Forbidden, $"status={(int)denied.StatusCode}");
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            _client?.Dispose();
            _handler?.Dispose();
            if (_host is not null)
            {
                await _host.StopAsync();
                await _host.DisposeAsync();
            }

            foreach (var schema in new[] { _application, _audit, _eventsOptions.Schema })
            {
                await using var command = _source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                _ = await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await _source.DisposeAsync();
        }
    }
}

internal sealed class CapacityScope(StudioDatabaseScope scope) : IStudioScopeResolver
{
    public ValueTask<StudioDatabaseScope> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(scope);
}

internal sealed class CapacityEventScope(StudioEventScope scope) : IStudioEventScopeResolver
{
    public ValueTask<StudioEventScope> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(scope);
}

// Disposable loopback fixture identity. A deployment must supply its own authentication.
internal sealed class CapacityAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Capacity-User"].ToString();
        if (user is not ("reader" or "operator")) { return Task.FromResult(AuthenticateResult.NoResult()); }
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "capacity-" + user) };
        if (user == "operator") { claims.Add(new Claim("query", "allowed")); }
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, "capacity")), "capacity")));
    }
}

using System.Data.Common;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BlueTusk.Data;
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

namespace BlueTusk.Edge.Tests;

public sealed class EdgeHttpIntegrationTests
{
    [Fact]
    public async Task Wire_codecs_preserve_Int64_precision_and_raw_payload_identity()
    {
        var scope = new EdgeScope("tenant", "orders", long.MaxValue);
        var mutation = new EdgeMutation(scope, Guid.NewGuid(), "1", long.MaxValue - 1, EdgeMutationKind.Upsert, "{ \"raw\" : true }"u8.ToArray());
        var encoded = EdgeWireCodec.SerializeMutation(mutation);
        var decoded = EdgeWireCodec.DeserializeMutation(scope, encoded, 1024);
        Assert.Equal(mutation.Fingerprint, decoded.Fingerprint);
        Assert.Equal(mutation.Payload.ToArray(), decoded.Payload.ToArray());
        Assert.Equal(long.MaxValue - 1, decoded.ExpectedRevision);
        Assert.Equal(long.MaxValue, EdgeWireCodec.DeserializeSnapshot(EdgeWireCodec.SerializeSnapshot(new EdgeSnapshot(Guid.NewGuid(), long.MaxValue))).Position);
        Assert.Throws<System.Text.Json.JsonException>(() => EdgeWireCodec.ParseNumber("9007199254740993.0"));
        Assert.Throws<System.Text.Json.JsonException>(() => EdgeWireCodec.ParseNumber("01"));
        Assert.Throws<System.Text.Json.JsonException>(() => EdgeWireCodec.DeserializeSnapshotPage("{}"u8, 512, 1024));
        Assert.Throws<System.Text.Json.JsonException>(() => EdgeWireCodec.DeserializeChanges("{\"fromPosition\":\"0\",\"toPosition\":\"1\",\"records\":[null]}"u8, 512, 1024));
        await Task.CompletedTask;
    }

    [Fact]
    public Task Actual_HTTP_authentication_scope_checks_and_mutation_identity_are_enforced() => WithFixtureAsync(async fixture =>
    {
        await using var host = await Host.StartAsync(fixture.Store);
        using var noCredentials = new HttpClient();
        using var noAuth = new HttpEdgeRemoteTransport(noCredentials, host.Endpoint);
        var denied = await Assert.ThrowsAsync<EdgeHttpTransportException>(async () => await noAuth.BeginSnapshotAsync(fixture.Scope));
        Assert.Equal(401, denied.StatusCode);
        using var client = Client();
        using var remote = new HttpEdgeRemoteTransport(client, host.Endpoint);
        var crossTenant = await Assert.ThrowsAsync<EdgeHttpTransportException>(async () => await remote.BeginSnapshotAsync(new EdgeScope("other", "orders", 1)));
        Assert.Equal(403, crossTenant.StatusCode);
        var crossScope = await Assert.ThrowsAsync<EdgeHttpTransportException>(async () => await remote.BeginSnapshotAsync(new EdgeScope("tenant", "private", 1)));
        Assert.Equal(403, crossScope.StatusCode);
        var mutation = new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", 0, EdgeMutationKind.Upsert, "{\"count\":1}"u8.ToArray());
        var applied = await remote.ApplyMutationAsync(mutation);
        Assert.Equal(EdgeMutationOutcomeKind.Applied, applied.Kind);
        Assert.Equal(applied.ServerRecord!.Revision, (await remote.ApplyMutationAsync(mutation)).ServerRecord!.Revision);
        var reused = await Assert.ThrowsAsync<EdgeHttpTransportException>(async () => await remote.ApplyMutationAsync(new EdgeMutation(fixture.Scope, mutation.Id, "other", 0, EdgeMutationKind.Upsert, "{}"u8.ToArray())));
        Assert.Equal(409, reused.StatusCode);
        var snapshot = await remote.BeginSnapshotAsync(fixture.Scope);
        var snapshots = new List<EdgeRecord>();
        await foreach (var batch in remote.ReadSnapshotAsync(fixture.Scope, snapshot)) { snapshots.AddRange(batch); }
        Assert.Single(snapshots);
        Assert.Null(await remote.ReadChangesAsync(fixture.Scope, snapshot.Position, 512));
    });

    [Fact]
    public Task Actual_HTTP_commit_disconnect_server_restart_and_Sqlite_reopen_recover_one_business_effect() => WithFixtureAsync(async fixture =>
    {
        await using (var setup = fixture.Source.CreateCommand($"CREATE TABLE \"{fixture.Store.Options.Schema}\".business_effects(id text PRIMARY KEY,counter integer NOT NULL)"))
        { _ = await setup.ExecuteNonQueryAsync(); }
        async ValueTask WriteBusinessAsync(DbConnection connection, DbTransaction transaction, EdgeMutation mutation, EdgeRecord record, CancellationToken cancellationToken)
        {
            Assert.Equal(mutation.DocumentId, record.Id);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"INSERT INTO \"{fixture.Store.Options.Schema}\".business_effects VALUES(@id,1) ON CONFLICT(id) DO UPDATE SET counter=business_effects.counter+1";
            var id = command.CreateParameter(); id.ParameterName = "id"; id.Value = mutation.DocumentId; command.Parameters.Add(id);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var clock = new ManualClock();
        var directory = Directory.CreateTempSubdirectory("bluetusk-edge-http-").FullName;
        try
        {
            var options = new SqliteEdgeOptions { DatabasePath = Path.Combine(directory, "edge.db"), TimeProvider = clock };
            var local = new SqliteEdgeStore(options);
            await local.InitializeAsync();
            await local.ActivateScopeAsync(fixture.Scope);
            var host = await Host.StartAsync(fixture.Store, writeBusiness: WriteBusinessAsync);
            var originalEndpoint = host.Endpoint;
            using var client = Client();
            using (var remote = new HttpEdgeRemoteTransport(client, host.Endpoint))
            {
                var coordinator = new EdgeSynchronizationCoordinator(local, remote);
                await coordinator.SynchronizeAsync(fixture.Scope);
                await local.EnqueueAsync(new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", 0, EdgeMutationKind.Upsert, "{\"count\":1}"u8.ToArray()));
                host.DropNextMutationResponse = true;
                _ = await Assert.ThrowsAnyAsync<Exception>(async () => await coordinator.SynchronizeAsync(fixture.Scope));
                Assert.Equal(1, (await fixture.Store.ReadChangesAsync(fixture.Scope, 0))!.ToPosition);
            }
            await host.DisposeAsync();
            await using var restarted = await Host.StartAsync(new PostgreSqlEdgeServerStore(fixture.Source, fixture.Store.Options), originalEndpoint, WriteBusinessAsync);
            clock.Advance(TimeSpan.FromMinutes(2));
            local = new SqliteEdgeStore(options);
            await local.InitializeAsync();
            using var recoveredRemote = new HttpEdgeRemoteTransport(client, restarted.Endpoint);
            await new EdgeSynchronizationCoordinator(local, recoveredRemote).SynchronizeAsync(fixture.Scope);
            Assert.Null((await local.GetAsync(fixture.Scope, "1"))!.PendingMutationId);
            Assert.Null(await local.ClaimAsync(fixture.Scope, TimeSpan.FromMinutes(1)));
            Assert.Equal(1, (await fixture.Store.ReadChangesAsync(fixture.Scope, 0))!.ToPosition);
            Assert.Equal(1, (await local.GetCheckpointAsync(fixture.Scope)).Position);
            await using var effects = fixture.Source.CreateCommand($"SELECT counter FROM \"{fixture.Store.Options.Schema}\".business_effects");
            Assert.Equal(1, await effects.ExecuteScalarAsync());
        }
        finally { DeleteOwnedDirectory(directory); }
    });

    [Fact]
    public Task Replay_expired_full_Sqlite_cache_refreshes_without_losing_a_queued_write() => WithFixtureAsync(async fixture =>
    {
        var first = await fixture.Store.ApplyMutationAsync(new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", 0, EdgeMutationKind.Upsert, "{\"value\":\"old\"}"u8.ToArray()));
        var second = await fixture.Store.ApplyMutationAsync(new EdgeMutation(fixture.Scope, Guid.NewGuid(), "2", 0, EdgeMutationKind.Upsert, "{\"value\":\"old\"}"u8.ToArray()));
        await using var host = await Host.StartAsync(fixture.Store);
        using var client = Client();
        using var remote = new HttpEdgeRemoteTransport(client, host.Endpoint);
        var directory = Directory.CreateTempSubdirectory("bluetusk-edge-http-").FullName;
        try
        {
            var local = new SqliteEdgeStore(new SqliteEdgeOptions
            {
                DatabasePath = Path.Combine(directory, "edge.db"),
                MaxCacheRecords = 2,
                MaxStagedRecords = 2,
            });
            await local.InitializeAsync(); await local.ActivateScopeAsync(fixture.Scope);
            var coordinator = new EdgeSynchronizationCoordinator(local, remote);
            await coordinator.SynchronizeAsync(fixture.Scope);
            Assert.Equal(2, (await local.ReadPageAsync(fixture.Scope)).Items.Count);
            var queued = new EdgeMutation(fixture.Scope, Guid.NewGuid(), "2", second.ServerRecord!.Revision, EdgeMutationKind.Upsert, "{\"value\":\"offline\"}"u8.ToArray());
            await local.EnqueueAsync(queued);
            _ = await fixture.Store.ApplyMutationAsync(new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", first.ServerRecord!.Revision, EdgeMutationKind.Upsert, "{\"value\":\"new\"}"u8.ToArray()));
            Assert.Equal(3, await fixture.Store.PruneChangesAsync(fixture.Scope, 3));
            var expired = await Assert.ThrowsAsync<EdgeHttpTransportException>(async () => await remote.ReadChangesAsync(fixture.Scope, 2, 512));
            Assert.Equal(410, expired.StatusCode);
            var snapshot = await remote.BeginSnapshotAsync(fixture.Scope);
            await local.BeginSnapshotAsync(fixture.Scope, snapshot);
            await foreach (var batch in remote.ReadSnapshotAsync(fixture.Scope, snapshot)) { await local.ApplySnapshotBatchAsync(fixture.Scope, snapshot.Id, batch); }
            Assert.Equal("{\"value\":\"old\"}", System.Text.Encoding.UTF8.GetString((await local.GetAsync(fixture.Scope, "1"))!.Payload.Span));
            await local.CommitSnapshotAsync(fixture.Scope, snapshot.Id);
            Assert.Equal(snapshot.Position, (await local.GetCheckpointAsync(fixture.Scope)).Position);
            Assert.Equal("{\"value\":\"new\"}", System.Text.Encoding.UTF8.GetString((await local.GetAsync(fixture.Scope, "1"))!.Payload.Span));
            Assert.Equal(queued.Id, (await local.GetAsync(fixture.Scope, "2"))!.PendingMutationId);
            await coordinator.SynchronizeAsync(fixture.Scope);
            Assert.Null((await local.GetAsync(fixture.Scope, "2"))!.PendingMutationId);
        }
        finally { DeleteOwnedDirectory(directory); }
    });

    [Fact]
    public Task Actual_HTTP_conflict_preserves_offline_payload_then_explicit_resolution_and_delete_sync() => WithFixtureAsync(async fixture =>
    {
        _ = await fixture.Store.ApplyMutationAsync(new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", 0, EdgeMutationKind.Upsert, "{\"count\":1}"u8.ToArray()));
        await using var host = await Host.StartAsync(fixture.Store);
        using var client = Client();
        using var remote = new HttpEdgeRemoteTransport(client, host.Endpoint);
        var directory = Directory.CreateTempSubdirectory("bluetusk-edge-http-").FullName;
        try
        {
            var local = new SqliteEdgeStore(new SqliteEdgeOptions { DatabasePath = Path.Combine(directory, "edge.db") });
            await local.InitializeAsync(); await local.ActivateScopeAsync(fixture.Scope);
            var coordinator = new EdgeSynchronizationCoordinator(local, remote);
            await coordinator.SynchronizeAsync(fixture.Scope);
            var before = (await local.GetAsync(fixture.Scope, "1"))!;
            var mutation = new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", before.ServerRevision, EdgeMutationKind.Upsert, "{\"count\":2}"u8.ToArray());
            await local.EnqueueAsync(mutation);
            _ = await fixture.Store.ApplyMutationAsync(new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", before.ServerRevision, EdgeMutationKind.Upsert, "{\"count\":3}"u8.ToArray()));
            await coordinator.SynchronizeAsync(fixture.Scope);
            var conflict = (await local.GetAsync(fixture.Scope, "1"))!;
            Assert.Equal(EdgeMutationStatus.Conflict, conflict.PendingStatus);
            Assert.Equal(mutation.Payload.ToArray(), conflict.Payload.ToArray());
            Assert.True(conflict.ServerRevision > before.ServerRevision);
            await local.ResolveConflictAsync(mutation.Id, new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", conflict.ServerRevision, EdgeMutationKind.Upsert, "{\"count\":4}"u8.ToArray()));
            await coordinator.SynchronizeAsync(fixture.Scope);
            var resolved = (await local.GetAsync(fixture.Scope, "1"))!;
            Assert.Null(resolved.PendingMutationId);
            await local.EnqueueAsync(new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", resolved.ServerRevision, EdgeMutationKind.Delete, default));
            await coordinator.SynchronizeAsync(fixture.Scope);
            Assert.True((await local.GetAsync(fixture.Scope, "1"))!.Deleted);
            Assert.Empty((await local.ReadPageAsync(fixture.Scope)).Items);
            Assert.True((await fixture.Store.GetAsync(fixture.Scope, "1"))!.Deleted);
        }
        finally { DeleteOwnedDirectory(directory); }
    });

    [Fact]
    public Task HTTP_confirmation_releases_outcome_without_reapplying_a_late_retry() => WithFixtureAsync(async fixture =>
    {
        var mutation = new EdgeMutation(fixture.Scope, Guid.NewGuid(), "confirmed", 0, EdgeMutationKind.Upsert, "{\"value\":1}"u8.ToArray());
        await using var host = await Host.StartAsync(fixture.Store);
        using var client = Client();
        using var remote = new HttpEdgeRemoteTransport(client, host.Endpoint);
        var first = await remote.ApplyMutationAsync(mutation);
        await remote.FinalizeMutationReceiptAsync(mutation);
        await remote.FinalizeMutationReceiptAsync(mutation);
        var error = await Assert.ThrowsAsync<EdgeHttpTransportException>(async () => await remote.ApplyMutationAsync(mutation));
        Assert.Equal(410, error.StatusCode);
        Assert.Equal(first.ServerRecord!.Revision, (await fixture.Store.GetAsync(fixture.Scope, "confirmed"))!.Revision);
        Assert.Equal(1, (await fixture.Store.ReadHealthAsync(fixture.Scope)).ReceiptCount);
        Assert.Equal(0, (await fixture.Store.ReadHealthAsync(fixture.Scope)).ReceiptBytes);
    });

    [Fact]
    public Task HTTP_ordered_horizon_reclaims_a_confirmed_receipt_without_reapplying_it() => WithFixtureAsync(async fixture =>
    {
        var stream = EdgeOrderedMutationId.NewStreamId();
        var id = EdgeOrderedMutationId.Create(stream, 1);
        var mutation = new EdgeMutation(fixture.Scope, id, "ordered", 0, EdgeMutationKind.Upsert, "{}"u8.ToArray());
        var gap = new EdgeMutation(fixture.Scope, EdgeOrderedMutationId.Create(stream, 2), "gap", 0, EdgeMutationKind.Upsert, "{}"u8.ToArray());
        await using var host = await Host.StartAsync(fixture.Store);
        using var client = Client(); using var remote = new HttpEdgeRemoteTransport(client, host.Endpoint);
        var missingPrefix = await Assert.ThrowsAsync<EdgeHttpTransportException>(async () => await remote.ApplyMutationAsync(gap));
        Assert.Equal(409, missingPrefix.StatusCode);
        _ = await remote.ApplyMutationAsync(mutation);
        var early = await Assert.ThrowsAsync<EdgeHttpTransportException>(async () => await remote.AdvanceOrderedReceiptHorizonAsync(fixture.Scope, id));
        Assert.Equal(409, early.StatusCode);
        await remote.FinalizeMutationReceiptAsync(mutation);
        await remote.AdvanceOrderedReceiptHorizonAsync(fixture.Scope, id);
        await remote.AdvanceOrderedReceiptHorizonAsync(fixture.Scope, id);
        Assert.Equal(0, (await fixture.Store.ReadHealthAsync(fixture.Scope)).ReceiptCount);
        var late = await Assert.ThrowsAsync<EdgeHttpTransportException>(async () => await remote.ApplyMutationAsync(mutation));
        Assert.Equal(410, late.StatusCode);
    });

    private static HttpClient Client()
    {
        var client = new HttpClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "tenant-reader"); return client;
    }
    private static async Task WithFixtureAsync(Func<Fixture, Task> test)
    {
        var connection = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") ?? throw new InvalidOperationException("Configure disposable PostgreSQL Edge server fixture.");
        await using var source = BlueTuskDataSource.Create(connection);
        var schema = "edge_http_" + Guid.NewGuid().ToString("N");
        await using var store = new PostgreSqlEdgeServerStore(source, new EdgeServerOptions { Schema = schema });
        try
        {
            await store.InitializeAsync(); var scope = new EdgeScope("tenant", "orders", 1); await store.ActivateScopeAsync(scope);
            await test(new(source, store, scope));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"); _ = await cleanup.ExecuteNonQueryAsync();
        }
    }
    private static void DeleteOwnedDirectory(string path)
    {
        var resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("bluetusk-edge-http-", StringComparison.Ordinal)) { throw new InvalidOperationException("Refusing cleanup outside owned test directory."); }
        Directory.Delete(resolved, true);
    }
    private sealed record Fixture(BlueTuskDataSource Source, PostgreSqlEdgeServerStore Store, EdgeScope Scope);
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan by) => _now += by;
    }
    private sealed class Host(WebApplication application, Uri endpoint) : IAsyncDisposable
    {
        internal Uri Endpoint { get; } = endpoint;
        internal bool DropNextMutationResponse { get; set; }
        internal static async Task<Host> StartAsync(PostgreSqlEdgeServerStore store, Uri? restartEndpoint = null,
            Func<DbConnection, DbTransaction, EdgeMutation, EdgeRecord, CancellationToken, ValueTask>? writeBusiness = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            var baseUrl = restartEndpoint is null ? "http://127.0.0.1:0" : restartEndpoint.GetLeftPart(UriPartial.Authority);
            builder.WebHost.UseKestrel().UseUrls(baseUrl);
            builder.Services.AddAuthentication("edge-test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("edge-test", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy("edge", policy => policy.AddAuthenticationSchemes("edge-test").RequireAuthenticatedUser()));
            var application = builder.Build();
            application.UseAuthentication(); application.UseAuthorization();
            Host? host = null;
            application.Use(async (context, next) =>
            {
                if (host?.DropNextMutationResponse is true && context.Request.Path == "/edge/mutations")
                {
                    host.DropNextMutationResponse = false;
                    context.Response.OnStarting(() => { context.Abort(); return Task.CompletedTask; });
                }
                await next(context);
            });
            application.MapBlueTuskEdge(store, static (context, scope, _) => ValueTask.FromResult(
                context.User.FindFirstValue("tenant") == scope.Tenant && scope.Id == "orders" && scope.Epoch == 1), new EdgeEndpointOptions { AuthorizationPolicy = "edge", WriteBusinessAsync = writeBusiness });
            await application.StartAsync();
            host = new Host(application, new Uri(application.Urls.Single().TrimEnd('/') + "/edge"));
            return host;
        }
        public async ValueTask DisposeAsync() { await application.StopAsync(); await application.DisposeAsync(); }
    }
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization != "Bearer tenant-reader") { return Task.FromResult(AuthenticateResult.NoResult()); }
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant", "tenant")], Scheme.Name)), Scheme.Name)));
        }
    }
}

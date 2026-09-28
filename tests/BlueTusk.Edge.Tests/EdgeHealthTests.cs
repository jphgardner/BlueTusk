using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BlueTusk.Data;
using BlueTusk.Edge.AspNetCore;
using BlueTusk.Edge.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlueTusk.Edge.Tests;

public sealed class EdgeHealthTests
{
    [Fact]
    public Task Scope_health_observes_committed_counters_replays_tombstones_pruning_and_epoch_isolation() => WithFixtureAsync(async fixture =>
    {
        var other = new EdgeScope("other", "orders", 1);
        await fixture.Store.ActivateScopeAsync(other);
        var mutation = new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", 0, EdgeMutationKind.Upsert, "{}"u8.ToArray());
        var applied = await fixture.Store.ApplyMutationAsync(mutation);
        _ = await fixture.Store.ApplyMutationAsync(mutation);
        _ = await fixture.Store.ApplyMutationAsync(new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", 0, EdgeMutationKind.Upsert, "{}"u8.ToArray()));
        _ = await fixture.Store.ApplyMutationAsync(new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", applied.ServerRecord!.Revision, EdgeMutationKind.Delete, default));
        var snapshot = await fixture.Store.BeginSnapshotAsync(fixture.Scope);
        var health = await fixture.Store.ReadHealthAsync(fixture.Scope);
        Assert.Equal(2, health.HeadPosition); Assert.Equal(0, health.ReplayFloor);
        Assert.Equal(1, health.RecordCount); Assert.Equal(0, health.RecordBytes);
        Assert.Equal(3, health.ReceiptCount); Assert.Equal(2, health.ChangeCount); Assert.Equal(2, health.ChangeBytes);
        Assert.Equal(1, health.ActiveSnapshots); Assert.False(health.MutationCapacityReached);
        Assert.InRange(health.DatabaseTime, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(0, (await fixture.Store.ReadHealthAsync(other)).RecordCount);
        Assert.Equal(2, await fixture.Store.PruneChangesAsync(fixture.Scope, 2));
        await fixture.Store.ReleaseSnapshotAsync(fixture.Scope, snapshot.Id);
        health = await fixture.Store.ReadHealthAsync(fixture.Scope);
        Assert.Equal(2, health.ReplayFloor); Assert.Equal(0, health.ChangeCount); Assert.Equal(0, health.ChangeBytes); Assert.Equal(0, health.ActiveSnapshots);
        var next = new EdgeScope("tenant", "orders", 2);
        await fixture.Store.ActivateScopeAsync(next);
        await Assert.ThrowsAsync<EdgeScopeMismatchException>(async () => await fixture.Store.ReadHealthAsync(fixture.Scope));
        Assert.Equal(0, (await fixture.Store.ReadHealthAsync(next)).ReceiptCount);
    });

    [Fact]
    public Task Capacity_health_uses_bounded_snapshot_observation_and_never_prunes_expired_state() => WithFixtureAsync(async fixture =>
    {
        await using var bounded = new PostgreSqlEdgeServerStore(fixture.Source, fixture.Store.Options with { MaxRecordsPerScope = 1, MaxSnapshotsPerScope = 1 });
        _ = await bounded.ApplyMutationAsync(new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", 0, EdgeMutationKind.Upsert, "{}"u8.ToArray()));
        _ = await fixture.Store.BeginSnapshotAsync(fixture.Scope);
        _ = await fixture.Store.BeginSnapshotAsync(fixture.Scope);
        _ = await fixture.Store.BeginSnapshotAsync(fixture.Scope);
        var health = await bounded.ReadHealthAsync(fixture.Scope);
        Assert.True(health.MutationCapacityReached); Assert.True(health.SnapshotCapacityReached);
        Assert.Equal(2, health.ActiveSnapshots); // Max+1 sentinel, rather than an unbounded count.
        await fixture.ExecuteAsync($"UPDATE {fixture.Schema}.snapshots SET expires_at=clock_timestamp()-interval '1 second'");
        Assert.Equal(0, (await bounded.ReadHealthAsync(fixture.Scope)).ActiveSnapshots);
        Assert.Equal(3L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.snapshots"));
    });

    [Fact]
    public Task Host_health_deadline_saturation_and_cancellation_recover_after_a_real_database_lock() => WithFixtureAsync(async fixture =>
    {
        var check = new EdgeServerHealthCheck(fixture.Store, fixture.Scope, new EdgeServerHealthCheckOptions { Timeout = TimeSpan.FromMilliseconds(150) });
        await using var connection = await fixture.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var hold = connection.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText = $"SELECT epoch FROM {fixture.Schema}.scopes FOR UPDATE";
            _ = await hold.ExecuteScalarAsync();
        }
        var waiting = check.CheckHealthAsync(new HealthCheckContext());
        var saturated = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, saturated.Status); Assert.Equal("edge_health_probe_saturated", saturated.Description);
        var timedOut = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HealthStatus.Unhealthy, timedOut.Status); Assert.Equal("edge_health_probe_timeout", timedOut.Description);
        Assert.Null(timedOut.Exception); Assert.Empty(timedOut.Data);
        await transaction.RollbackAsync();
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        HealthCheckResult ready;
        do { await Task.Delay(10, recovery.Token); ready = await check.CheckHealthAsync(new HealthCheckContext(), recovery.Token); }
        while (ready.Status is not HealthStatus.Healthy);
        await using var second = await connection.BeginTransactionAsync();
        await using (var hold = connection.CreateCommand())
        {
            hold.Transaction = second; hold.CommandText = $"SELECT epoch FROM {fixture.Schema}.scopes FOR UPDATE";
            _ = await hold.ExecuteScalarAsync();
        }
        using var cancellation = new CancellationTokenSource();
        waiting = check.CheckHealthAsync(new HealthCheckContext(), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await second.RollbackAsync();
    });

    [Fact]
    public Task Missing_scope_format_drift_and_disposal_emit_sanitized_health_and_status_only_metrics() => WithFixtureAsync(async fixture =>
    {
        var tags = new ConcurrentBag<KeyValuePair<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == EdgeServerHealthCheck.MeterName) { owner.EnableMeasurementEvents(instrument); } };
        listener.SetMeasurementEventCallback<long>((_, value, measurements, _) => { Assert.True(value >= 0); foreach (var tag in measurements) { tags.Add(tag); } });
        listener.SetMeasurementEventCallback<double>((_, value, measurements, _) => { Assert.True(value >= 0); foreach (var tag in measurements) { tags.Add(tag); } });
        listener.Start();
        var check = new EdgeServerHealthCheck(fixture.Store, fixture.Scope);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        var wrong = await new EdgeServerHealthCheck(fixture.Store, new EdgeScope("secret-tenant", "secret-scope", 1)).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal("edge_scope_unavailable", wrong.Description); Assert.Null(wrong.Exception); Assert.Empty(wrong.Data);
        await fixture.ExecuteAsync($"UPDATE {fixture.Schema}.metadata SET version=999");
        var drift = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal("edge_store_unavailable", drift.Description); Assert.Null(drift.Exception); Assert.Empty(drift.Data);
        await fixture.ExecuteAsync($"UPDATE {fixture.Schema}.metadata SET version=1");
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        await fixture.Store.DisposeAsync();
        var disposed = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal("edge_store_unavailable", disposed.Description); Assert.Null(disposed.Exception);
        Assert.NotEmpty(tags);
        Assert.All(tags, static tag => { Assert.Equal("status", tag.Key); Assert.Contains(tag.Value, new object[] { "healthy", "degraded", "unhealthy", "cancelled" }); });
    });

    [Fact]
    public Task Timed_out_uncooperative_connection_open_keeps_admission_reserved_until_late_completion() => WithFixtureAsync(async fixture =>
    {
        await using var delayed = new DelayedSource(fixture.Source);
        await using var store = new PostgreSqlEdgeServerStore(delayed, fixture.Store.Options);
        var check = new EdgeServerHealthCheck(store, fixture.Scope, new EdgeServerHealthCheckOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        var first = check.CheckHealthAsync(new HealthCheckContext());
        await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var timedOut = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("edge_health_probe_timeout", timedOut.Description);
        for (var i = 0; i < 3; i++) { Assert.Equal("edge_health_probe_saturated", (await check.CheckHealthAsync(new HealthCheckContext())).Description); }
        Assert.Equal(1, delayed.Opens);
        delayed.Release.TrySetResult();
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        HealthCheckResult ready;
        do { await Task.Delay(10, recovery.Token); ready = await check.CheckHealthAsync(new HealthCheckContext(), recovery.Token); }
        while (ready.Status is not HealthStatus.Healthy);
        Assert.Equal(2, delayed.Opens);
    });

    [Fact]
    public Task Real_authorized_ASP_health_endpoint_reports_readiness_capacity_drift_and_recovery() => WithFixtureAsync(async fixture =>
    {
        await using var bounded = new PostgreSqlEdgeServerStore(fixture.Source, fixture.Store.Options with { MaxRecordsPerScope = 1 });
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("Operator").AddScheme<AuthenticationSchemeOptions, OperatorAuthentication>("Operator", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("operators", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddHealthChecks().AddBlueTuskEdgeServer("edge-readiness", bounded, fixture.Scope);
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapHealthChecks("/ops/edge", new HealthCheckOptions { ResultStatusCodes = { [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable } }).RequireAuthorization("operators");
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using (var anonymous = await http.GetAsync("/ops/edge")) { Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "operator-test");
        using (var healthy = await http.GetAsync("/ops/edge?tenant=untrusted")) { Assert.Equal(HttpStatusCode.OK, healthy.StatusCode); Assert.Equal("Healthy", await healthy.Content.ReadAsStringAsync()); }
        _ = await bounded.ApplyMutationAsync(new EdgeMutation(fixture.Scope, Guid.NewGuid(), "1", 0, EdgeMutationKind.Upsert, "{\"secret\":true}"u8.ToArray()));
        using (var capacity = await http.GetAsync("/ops/edge")) { Assert.Equal(HttpStatusCode.ServiceUnavailable, capacity.StatusCode); Assert.Equal("Degraded", await capacity.Content.ReadAsStringAsync()); }
        await fixture.ExecuteAsync($"UPDATE {fixture.Schema}.metadata SET version=999");
        using (var drift = await http.GetAsync("/ops/edge")) { Assert.Equal(HttpStatusCode.ServiceUnavailable, drift.StatusCode); Assert.Equal("Unhealthy", await drift.Content.ReadAsStringAsync()); }
        await fixture.ExecuteAsync($"UPDATE {fixture.Schema}.metadata SET version=1");
        var next = new EdgeScope("tenant", "orders", 2);
        await bounded.ActivateScopeAsync(next);
        var service = app.Services.GetRequiredService<HealthCheckService>();
        var stale = await service.CheckHealthAsync(); Assert.Equal(HealthStatus.Unhealthy, stale.Status);
        Assert.Equal(HealthStatus.Healthy, (await new EdgeServerHealthCheck(bounded, next).CheckHealthAsync(new HealthCheckContext())).Status);
        await app.StopAsync();
    });

    private static async Task WithFixtureAsync(Func<Fixture, Task> test)
    {
        var connection = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") ?? throw new InvalidOperationException("Configure disposable Edge PostgreSQL.");
        await using var source = BlueTuskDataSource.Create(connection);
        var schema = "edge_health_" + Guid.NewGuid().ToString("N");
        await using var store = new PostgreSqlEdgeServerStore(source, new EdgeServerOptions { Schema = schema });
        var scope = new EdgeScope("tenant", "orders", 1);
        try { await store.InitializeAsync(); await store.ActivateScopeAsync(scope); await test(new(source, store, scope)); }
        finally { await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"); _ = await command.ExecuteNonQueryAsync(); }
    }
    private sealed record Fixture(BlueTuskDataSource Source, PostgreSqlEdgeServerStore Store, EdgeScope Scope)
    {
        internal string Schema => '"' + Store.Options.Schema + '"';
        internal async Task ExecuteAsync(string sql) { await using var command = Source.CreateCommand(sql); _ = await command.ExecuteNonQueryAsync(); }
        internal async Task<object?> ScalarAsync(string sql) { await using var command = Source.CreateCommand(sql); return await command.ExecuteScalarAsync(); }
    }
    private sealed class OperatorAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers.Authorization == "Bearer operator-test"
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.NoResult());
    }
    private sealed class DelayedSource(BlueTuskDataSource source) : DbDataSource
    {
        private int _opens;
        public override string ConnectionString => source.ConnectionString;
        internal int Opens => Volatile.Read(ref _opens);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override DbConnection CreateDbConnection() => source.CreateConnection();
        protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _opens) == 1)
            {
                Started.TrySetResult(); await Release.Task; // Deliberately ignores cancellation before delegating to real PostgreSQL.
                return await source.OpenConnectionAsync(CancellationToken.None);
            }
            return await source.OpenConnectionAsync(cancellationToken);
        }
    }
}

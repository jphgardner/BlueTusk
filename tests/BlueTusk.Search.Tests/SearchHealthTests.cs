using System.Data.Common;
using System.Net;
using BlueTusk.Search.AspNetCore;
using BlueTusk.Testing.OperatorHealth;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Search.Tests;

public sealed class SearchHealthTests
{
    [Fact]
    public Task Indexed_scope_probe_and_query_retention_run_under_payload_denied_read_only_role() => WithStoreAsync(async (database, store) =>
    {
        _ = await store.UpsertAsync(new SearchDocument("tenant", "index", "1", 1, "private", "private payload"));
        Assert.True((await store.ReadHealthAsync(new SearchScope("tenant", "index"))).HasDocuments);
        Assert.False((await store.ReadHealthAsync(new SearchScope("other", "index"))).HasDocuments);
        Assert.False((await store.ReadHealthAsync(new SearchScope("tenant", "other"))).HasDocuments);
        await database.WithReaderRoleAsync($"GRANT SELECT ON {database.Schema}.storage_metadata TO {database.Role}; GRANT SELECT(tenant,index_name) ON {database.Schema}.documents TO {database.Role}; GRANT SELECT(tenant,index_name,expires_at) ON {database.Schema}.queries TO {database.Role}", async source =>
        {
            await using var readerStore = new PostgreSqlSearchStore(source, store.Options);
            Assert.True((await readerStore.ReadHealthAsync(new SearchScope("tenant", "index"))).HasDocuments);
            await using var connection = await source.OpenConnectionAsync();
            await using var denied = connection.CreateCommand(); denied.CommandText = $"SELECT title,metadata FROM {database.Schema}.documents";
            await Assert.ThrowsAnyAsync<DbException>(async () => await denied.ExecuteScalarAsync());
            Assert.Equal(HealthStatus.Healthy, (await new SearchStoreHealthCheck(readerStore, new SearchScope("tenant", "index")).CheckHealthAsync(new HealthCheckContext())).Status);
        });
    });

    [Fact]
    public Task Retention_capacity_is_bounded_scope_isolated_and_expired_queries_are_observed_without_cleanup() => WithStoreAsync(async (database, store) =>
    {
        await using var bounded = new PostgreSqlSearchStore(database.Source, store.Options with { MaxActiveQueriesPerScope = 1 });
        var scope = new SearchScope("tenant", "index");
        _ = await store.SearchAsync(scope, new SearchRequest { Text = "unused" });
        _ = await store.SearchAsync(scope, new SearchRequest { Text = "unused" });
        _ = await store.SearchAsync(scope, new SearchRequest { Text = "unused" });
        var health = await bounded.ReadHealthAsync(scope);
        Assert.Equal(2, health.ObservedRetainedQueries); Assert.Equal(2, health.ObservedActiveQueries); Assert.True(health.QueryCapacityReached);
        Assert.Equal(0, (await bounded.ReadHealthAsync(new SearchScope("other", "index"))).ObservedRetainedQueries);
        await database.ExecuteAsync($"UPDATE {database.Schema}.queries SET expires_at=clock_timestamp()-interval '1 second'");
        health = await bounded.ReadHealthAsync(scope);
        Assert.Equal(0, health.ObservedActiveQueries); Assert.Equal(2, health.ObservedRetainedQueries); Assert.True(health.QueryCapacityReached);
        Assert.Equal(3L, await database.ScalarAsync($"SELECT count(*) FROM {database.Schema}.queries"));
        Assert.Equal(HealthStatus.Degraded, (await new SearchStoreHealthCheck(bounded, scope).CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.Equal(3, await bounded.PruneExpiredQueriesAsync());
        Assert.False((await bounded.ReadHealthAsync(scope)).QueryCapacityReached);
    });

    [Fact]
    public Task Metadata_lock_deadline_admission_cancellation_and_recovery_are_bounded() => WithStoreAsync(async (database, store) =>
    {
        var check = new SearchStoreHealthCheck(store, new SearchScope("tenant", "index"), new SearchStoreHealthCheckOptions { Timeout = TimeSpan.FromMilliseconds(150) });
        await database.WithMetadataLockAsync(async () =>
        {
            var waiting = check.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal("search_health_probe_saturated", (await check.CheckHealthAsync(new HealthCheckContext())).Description);
            var timeout = await waiting.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("search_health_probe_timeout", timeout.Description); Assert.Null(timeout.Exception); Assert.Empty(timeout.Data);
        });
        await AssertRecoveredAsync(check);
        await database.WithMetadataLockAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var waiting = check.CheckHealthAsync(new HealthCheckContext(), cancellation.Token); await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        });
        await AssertRecoveredAsync(check);
    });

    [Fact]
    public Task Missing_metadata_storage_and_model_chunk_contract_drift_are_redacted_with_fixed_metrics() => WithStoreAsync(async (database, store) =>
    {
        using var metrics = new StatusMetrics(SearchStoreHealthCheck.MeterName);
        var check = new SearchStoreHealthCheck(store, new SearchScope("tenant", "index"));
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        await database.ExecuteAsync($"UPDATE {database.Schema}.storage_metadata SET storage_version=999"); AssertRedacted(await check.CheckHealthAsync(new HealthCheckContext()));
        await database.ExecuteAsync($"UPDATE {database.Schema}.storage_metadata SET storage_version=1");
        await using var different = new PostgreSqlSearchStore(database.Source, store.Options with { MaxChunkCharacters = store.Options.MaxChunkCharacters + 1 });
        AssertRedacted(await new SearchStoreHealthCheck(different, new SearchScope("tenant", "index")).CheckHealthAsync(new HealthCheckContext()));
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        await database.ExecuteAsync($"DELETE FROM {database.Schema}.storage_metadata"); AssertRedacted(await check.CheckHealthAsync(new HealthCheckContext()));
        await store.DisposeAsync(); AssertRedacted(await check.CheckHealthAsync(new HealthCheckContext())); metrics.AssertOnlyFixedStatus();
    });

    [Fact]
    public Task Late_uncooperative_connection_open_keeps_probe_admission_reserved_then_recovers() => WithStoreAsync(async (database, store) =>
    {
        await using var source = new LateOpenSource(database.Source);
        await using var delayedStore = new PostgreSqlSearchStore(source, store.Options);
        await source.AssertBoundedAsync(new SearchStoreHealthCheck(delayedStore, new SearchScope("tenant", "index"), new SearchStoreHealthCheckOptions { Timeout = TimeSpan.FromMilliseconds(100) }), "search");
    });

    [Fact]
    public Task Actual_operator_role_endpoint_ignores_client_scope_and_reports_capacity_and_contract_drift() => WithStoreAsync(async (database, store) =>
    {
        await using var bounded = new PostgreSqlSearchStore(database.Source, store.Options with { MaxActiveQueriesPerScope = 1 });
        var scope = new SearchScope("tenant", "index");
        await using var host = await OperatorHealthHost.StartAsync(builder => builder.AddBlueTuskSearch("search-ready", bounded, scope));
        await host.AssertRolesAsync();
        _ = await bounded.SearchAsync(scope, new SearchRequest { Text = "unused" });
        await host.AssertStatusAsync(HttpStatusCode.ServiceUnavailable, "Degraded");
        await database.ExecuteAsync($"UPDATE {database.Schema}.storage_metadata SET storage_version=999"); await host.AssertStatusAsync(HttpStatusCode.ServiceUnavailable, "Unhealthy");
        await database.ExecuteAsync($"UPDATE {database.Schema}.storage_metadata SET storage_version=1; UPDATE {database.Schema}.queries SET expires_at=clock_timestamp()-interval '1 second'");
        _ = await bounded.PruneExpiredQueriesAsync(); await host.AssertStatusAsync(HttpStatusCode.OK, "Healthy");
    });

    private static void AssertRedacted(HealthCheckResult result)
    { Assert.Equal(HealthStatus.Unhealthy, result.Status); Assert.Equal("search_store_unavailable", result.Description); Assert.Null(result.Exception); Assert.Empty(result.Data); }
    private static async Task AssertRecoveredAsync(SearchStoreHealthCheck check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); HealthCheckResult result;
        do { await Task.Delay(10, timeout.Token); result = await check.CheckHealthAsync(new HealthCheckContext(), timeout.Token); } while (result.Status is not HealthStatus.Healthy);
    }
    private static Task WithStoreAsync(Func<HealthDatabase, PostgreSqlSearchStore, Task> test) => HealthDatabase.RunAsync("search_health_", async database =>
    { await using var store = new PostgreSqlSearchStore(database.Source, new SearchStoreOptions { Schema = database.SchemaName }); await store.InitializeAsync(); await test(database, store); });
}

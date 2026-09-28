using System.Data.Common;
using System.Net;
using BlueTusk.Documents.AspNetCore;
using BlueTusk.Testing.OperatorHealth;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Documents.Tests;

public sealed class DocumentHealthTests
{
    [Fact]
    public Task Indexed_scope_probe_runs_under_payload_denied_read_only_role_and_separates_tenants() => WithStoreAsync(async (database, store) =>
    {
        await database.ExecuteAsync($"INSERT INTO {database.Schema}.documents(tenant,collection,id,revision,schema_version,body) VALUES('tenant','orders','1',1,1,'{{\"private\":true}}')");
        Assert.True((await store.ReadHealthAsync("tenant", "orders")).HasDocuments);
        Assert.False((await store.ReadHealthAsync("other", "orders")).HasDocuments);
        Assert.False((await store.ReadHealthAsync("tenant", "other")).HasDocuments);
        await database.WithReaderRoleAsync($"GRANT SELECT ON {database.Schema}.storage_metadata TO {database.Role}; GRANT SELECT(tenant,collection) ON {database.Schema}.documents TO {database.Role}", async source =>
        {
            await using var readerStore = new DocumentStore(source, store.Options);
            Assert.True((await readerStore.ReadHealthAsync("tenant", "orders")).HasDocuments);
            await using var connection = await source.OpenConnectionAsync();
            await using var denied = connection.CreateCommand(); denied.CommandText = $"SELECT body FROM {database.Schema}.documents";
            await Assert.ThrowsAnyAsync<DbException>(async () => await denied.ExecuteScalarAsync());
            Assert.Equal(HealthStatus.Healthy, (await new DocumentStoreHealthCheck(readerStore, "tenant", "orders").CheckHealthAsync(new HealthCheckContext())).Status);
        });
    });

    [Fact]
    public Task Metadata_lock_deadline_nonqueued_admission_cancellation_and_recovery_are_bounded() => WithStoreAsync(async (database, store) =>
    {
        var check = new DocumentStoreHealthCheck(store, "tenant", "orders", new DocumentStoreHealthCheckOptions { Timeout = TimeSpan.FromMilliseconds(150) });
        await database.WithMetadataLockAsync(async () =>
        {
            var waiting = check.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal("documents_health_probe_saturated", (await check.CheckHealthAsync(new HealthCheckContext())).Description);
            var timeout = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("documents_health_probe_timeout", timeout.Description); Assert.Null(timeout.Exception); Assert.Empty(timeout.Data);
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
    public Task Missing_metadata_format_byte_contract_and_disposal_use_redacted_failures_and_fixed_metrics() => WithStoreAsync(async (database, store) =>
    {
        using var metrics = new StatusMetrics(DocumentStoreHealthCheck.MeterName);
        var check = new DocumentStoreHealthCheck(store, "tenant", "orders");
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        await database.ExecuteAsync($"UPDATE {database.Schema}.storage_metadata SET storage_version=999");
        AssertRedacted(await check.CheckHealthAsync(new HealthCheckContext()));
        await database.ExecuteAsync($"UPDATE {database.Schema}.storage_metadata SET storage_version=1,max_document_bytes=max_document_bytes+1");
        AssertRedacted(await check.CheckHealthAsync(new HealthCheckContext()));
        await database.ExecuteAsync($"UPDATE {database.Schema}.storage_metadata SET max_document_bytes={store.Options.MaxDocumentBytes}");
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        await database.ExecuteAsync($"DELETE FROM {database.Schema}.storage_metadata");
        AssertRedacted(await check.CheckHealthAsync(new HealthCheckContext()));
        await store.DisposeAsync(); AssertRedacted(await check.CheckHealthAsync(new HealthCheckContext())); metrics.AssertOnlyFixedStatus();
    });

    [Fact]
    public Task Late_uncooperative_connection_open_keeps_probe_admission_reserved_then_recovers() => WithStoreAsync(async (database, store) =>
    {
        await using var source = new LateOpenSource(database.Source);
        await using var delayedStore = new DocumentStore(source, store.Options);
        await source.AssertBoundedAsync(new DocumentStoreHealthCheck(delayedStore, "tenant", "orders", new DocumentStoreHealthCheckOptions { Timeout = TimeSpan.FromMilliseconds(100) }), "documents");
    });

    [Fact]
    public Task Actual_operator_role_endpoint_ignores_client_scope_and_recovers_after_format_drift() => WithStoreAsync(async (database, store) =>
    {
        await using var host = await OperatorHealthHost.StartAsync(builder => builder.AddBlueTuskDocuments("document-ready", store, "tenant", "orders"));
        await host.AssertRolesAsync();
        await database.ExecuteAsync($"UPDATE {database.Schema}.storage_metadata SET storage_version=999");
        await host.AssertStatusAsync(HttpStatusCode.ServiceUnavailable, "Unhealthy");
        await database.ExecuteAsync($"UPDATE {database.Schema}.storage_metadata SET storage_version=1");
        await host.AssertStatusAsync(HttpStatusCode.OK, "Healthy");
    });

    private static void AssertRedacted(HealthCheckResult result)
    { Assert.Equal(HealthStatus.Unhealthy, result.Status); Assert.Equal("documents_store_unavailable", result.Description); Assert.Null(result.Exception); Assert.Empty(result.Data); }
    private static async Task AssertRecoveredAsync(DocumentStoreHealthCheck check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); HealthCheckResult result;
        do { await Task.Delay(10, timeout.Token); result = await check.CheckHealthAsync(new HealthCheckContext(), timeout.Token); } while (result.Status is not HealthStatus.Healthy);
    }
    private static Task WithStoreAsync(Func<HealthDatabase, DocumentStore, Task> test) => HealthDatabase.RunAsync("documents_health_", async database =>
    { await using var store = new DocumentStore(database.Source, new DocumentStoreOptions { Schema = database.SchemaName }); await store.InitializeAsync(); await test(database, store); });
}

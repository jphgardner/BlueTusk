using BlueTusk.Data;

namespace BlueTusk.Studio.Tests;

public sealed class StudioAuditSinkTests
{
    [Fact]
    public async Task Durable_attempts_survive_reopen_retries_are_exact_and_future_versions_are_rejected()
    {
        await using var dataSource = BlueTuskDataSource.Create(StudioQueryServiceTests.ConnectionString());
        var schema = "studio_audit_" + Guid.NewGuid().ToString("N");
        try
        {
            var sink = new PostgreSqlStudioAuditSink(dataSource, schema);
            await sink.InitializeAsync(TestContext.Current.CancellationToken);
            var record = new StudioAuditRecord(Guid.NewGuid(), "principal-a", new string('a', 64), "attempt", 0);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => sink.RecordAsync(record, TestContext.Current.CancellationToken).AsTask()));
            await using var reopened = BlueTuskDataSource.Create(StudioQueryServiceTests.ConnectionString());
            var recovered = new PostgreSqlStudioAuditSink(reopened, schema);
            await recovered.InitializeAsync(TestContext.Current.CancellationToken);
            await recovered.RecordAsync(record, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.RecordAsync(record with { ActorId = "principal-b" }, TestContext.Current.CancellationToken).AsTask());
            await recovered.RecordAsync(record with { Outcome = "completed", ReturnedRows = 3 }, TestContext.Current.CancellationToken);
            await using (var count = dataSource.CreateCommand($"SELECT count(*) FROM \"{schema}\".studio_audit"))
            {
                Assert.Equal(2L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
            await using (var future = dataSource.CreateCommand($"UPDATE \"{schema}\".studio_audit_version SET version=2"))
            {
                _ = await future.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.RecordAsync(record with { OperationId = Guid.NewGuid() }, TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            await using var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            _ = await drop.ExecuteNonQueryAsync();
        }
    }
}

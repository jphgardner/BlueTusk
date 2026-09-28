using BlueTusk.Data;

namespace BlueTusk.Studio.Tests;

public sealed class StudioAuditSinkTests
{
    [Fact]
    public async Task Known_v1_audits_upgrade_with_explicit_unattributed_history()
    {
        await using var dataSource = BlueTuskDataSource.Create(StudioQueryServiceTests.ConnectionString());
        var schema = "studio_audit_v1_" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var legacy = dataSource.CreateCommand($"""
                CREATE SCHEMA "{schema}";
                CREATE TABLE "{schema}".studio_audit_version(singleton boolean PRIMARY KEY CHECK(singleton), version integer NOT NULL CHECK(version > 0));
                INSERT INTO "{schema}".studio_audit_version VALUES(true, 1);
                CREATE TABLE "{schema}".studio_audit(
                    operation_id uuid NOT NULL, outcome text NOT NULL, actor_id text NOT NULL,
                    query_fingerprint text NOT NULL, returned_rows integer NOT NULL,
                    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(), PRIMARY KEY(operation_id, outcome));
                INSERT INTO "{schema}".studio_audit(operation_id,outcome,actor_id,query_fingerprint,returned_rows)
                    VALUES('11111111-1111-1111-1111-111111111111','attempt','legacy-actor',repeat('a',64),0)
                """))
            {
                _ = await legacy.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var sink = new PostgreSqlStudioAuditSink(dataSource, schema);
            await sink.InitializeAsync(TestContext.Current.CancellationToken);
            await using (var migrated = dataSource.CreateCommand($"SELECT version FROM \"{schema}\".studio_audit_version"))
            {
                Assert.Equal(3, await migrated.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
            await using (var historical = dataSource.CreateCommand($"SELECT scope_id FROM \"{schema}\".studio_audit WHERE actor_id='legacy-actor'"))
            {
                Assert.Equal("legacy-unknown", await historical.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
            await sink.RecordAsync(new(Guid.CreateVersion7(), "current-actor", new string('b', 64), "attempt", 0)
            { ScopeId = "tenant-a-database" }, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() => sink.RecordAsync(new(Guid.CreateVersion7(), "current-actor", new string('b', 64), "attempt", 0),
                TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(0, await sink.PruneArchivedAsync(1, TestContext.Current.CancellationToken));
            await sink.ConfirmLegacyArchiveAsync("verified-legacy-export", TestContext.Current.CancellationToken);
            Assert.Equal(1, await sink.PruneArchivedAsync(1, TestContext.Current.CancellationToken));
            await using (var historicalCount = dataSource.CreateCommand($"SELECT count(*) FROM \"{schema}\".studio_audit WHERE actor_id='legacy-actor'"))
            {
                Assert.Equal(0L, await historicalCount.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            await using var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            _ = await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Sealed_horizon_rejects_late_retries_and_prunes_only_confirmed_archive_in_batches()
    {
        await using var dataSource = BlueTuskDataSource.Create(StudioQueryServiceTests.ConnectionString());
        var schema = "studio_audit_retention_" + Guid.NewGuid().ToString("N");
        try
        {
            var sink = new PostgreSqlStudioAuditSink(dataSource, schema);
            await sink.InitializeAsync(TestContext.Current.CancellationToken);
            var oldTime = DateTimeOffset.UtcNow.AddMinutes(-2);
            var old = new StudioAuditRecord(Guid.CreateVersion7(oldTime), "principal-a", new string('a', 64), "attempt", 0)
            { ScopeId = "tenant-a-database" };
            var newer = old with { OperationId = Guid.CreateVersion7(), Outcome = "completed", ReturnedRows = 1 };
            await sink.RecordAsync(old, TestContext.Current.CancellationToken);
            await sink.RecordAsync(newer, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() => sink.RecordAsync(old with { OperationId = Guid.NewGuid() }, TestContext.Current.CancellationToken).AsTask());
            var horizon = DateTimeOffset.UtcNow.AddMinutes(-1);
            await using (var inFlight = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
            await using (var inFlightTransaction = await inFlight.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                await using (var hold = inFlight.CreateCommand())
                {
                    hold.Transaction = inFlightTransaction;
                    hold.CommandText = $"SELECT sealed_through_ms FROM \"{schema}\".studio_audit_version WHERE singleton FOR SHARE";
                    _ = await hold.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                }
                var seal = sink.SealRetentionHorizonAsync(horizon, TestContext.Current.CancellationToken).AsTask();
                await Task.Delay(100, TestContext.Current.CancellationToken);
                Assert.False(seal.IsCompleted);
                await inFlightTransaction.CommitAsync(TestContext.Current.CancellationToken);
                await seal;
            }
            await Assert.ThrowsAsync<StudioAuditHorizonException>(() => sink.RecordAsync(old, TestContext.Current.CancellationToken).AsTask());
            await Assert.ThrowsAsync<StudioAuditHorizonException>(() => sink.RecordAsync(old with { ActorId = "changed" }, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(0, await sink.PruneArchivedAsync(1, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => sink.ConfirmArchivedHorizonAsync(DateTimeOffset.UtcNow, "unsealed", TestContext.Current.CancellationToken).AsTask());
            await sink.ConfirmArchivedHorizonAsync(horizon, "verified-export-2026-09-28", TestContext.Current.CancellationToken);
            Assert.Equal(1, await sink.PruneArchivedAsync(1, TestContext.Current.CancellationToken));
            Assert.Equal(0, await sink.PruneArchivedAsync(1, TestContext.Current.CancellationToken));
            await using (var remaining = dataSource.CreateCommand($"SELECT operation_id FROM \"{schema}\".studio_audit"))
            {
                Assert.Equal(newer.OperationId, await remaining.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
            await Assert.ThrowsAsync<StudioAuditHorizonException>(() => sink.RecordAsync(old, TestContext.Current.CancellationToken).AsTask());
            await using (var bypass = dataSource.CreateCommand($"""
                INSERT INTO "{schema}".studio_audit(operation_id,outcome,actor_id,scope_id,query_fingerprint,returned_rows,operation_time_ms)
                VALUES('{old.OperationId}'::uuid,'late','principal-a','tenant-a-database',repeat('a',64),0,{oldTime.ToUnixTimeMilliseconds()})
                """))
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() => bypass.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
                Assert.Contains("outside its writable horizon", error.ToString(), StringComparison.Ordinal);
            }
            await sink.RecordAsync(newer, TestContext.Current.CancellationToken);
        }
        finally
        {
            await using var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            _ = await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Durable_attempts_survive_reopen_retries_are_exact_and_future_versions_are_rejected()
    {
        await using var dataSource = BlueTuskDataSource.Create(StudioQueryServiceTests.ConnectionString());
        var schema = "studio_audit_" + Guid.NewGuid().ToString("N");
        try
        {
            var sink = new PostgreSqlStudioAuditSink(dataSource, schema);
            await sink.InitializeAsync(TestContext.Current.CancellationToken);
            var record = new StudioAuditRecord(Guid.CreateVersion7(), "principal-a", new string('a', 64), "attempt", 0) { ScopeId = "tenant-a-database" };
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => sink.RecordAsync(record, TestContext.Current.CancellationToken).AsTask()));
            async Task<string> TupleIdentityAsync()
            {
                await using var identity = dataSource.CreateCommand($"SELECT ctid::text || ':' || xmin::text FROM \"{schema}\".studio_audit WHERE operation_id=@operation AND outcome='attempt'");
                identity.Parameters.Add(new BlueTuskParameter<Guid>(record.OperationId) { ParameterName = "operation" });
                return Assert.IsType<string>(await identity.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
            var beforeRetry = await TupleIdentityAsync();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => sink.RecordAsync(record, TestContext.Current.CancellationToken).AsTask()));
            Assert.Equal(beforeRetry, await TupleIdentityAsync());
            await using var reopened = BlueTuskDataSource.Create(StudioQueryServiceTests.ConnectionString());
            var recovered = new PostgreSqlStudioAuditSink(reopened, schema);
            await recovered.InitializeAsync(TestContext.Current.CancellationToken);
            await recovered.RecordAsync(record, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.RecordAsync(record with { ActorId = "principal-b" }, TestContext.Current.CancellationToken).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.RecordAsync(record with { ScopeId = "tenant-b-database" }, TestContext.Current.CancellationToken).AsTask());
            await recovered.RecordAsync(record with { Outcome = "completed", ReturnedRows = 3 }, TestContext.Current.CancellationToken);
            await using (var count = dataSource.CreateCommand($"SELECT count(*) FROM \"{schema}\".studio_audit"))
            {
                Assert.Equal(2L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
            await using (var future = dataSource.CreateCommand($"UPDATE \"{schema}\".studio_audit_version SET version=4"))
            {
                _ = await future.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.RecordAsync(record with { OperationId = Guid.CreateVersion7() }, TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            await using var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            _ = await drop.ExecuteNonQueryAsync();
        }
    }
}

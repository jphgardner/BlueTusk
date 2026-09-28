using System.Data.Common;
using BlueTusk.Data;
using BlueTusk.Edge.Server;

namespace BlueTusk.Edge.Tests;

public sealed class EdgeServerStoreTests
{
    [Fact]
    public Task Application_table_callback_failure_rolls_back_all_state_and_receipt_replay_never_repeats_business_effects() => WithFixtureAsync(async fixture =>
    {
        var scope = new EdgeScope("tenant", "orders", 1); await fixture.Store.ActivateScopeAsync(scope);
        await using (var setup = fixture.Source.CreateCommand($"CREATE TABLE \"{fixture.Store.Options.Schema}\".business_effects(tenant text NOT NULL,id text NOT NULL,counter integer NOT NULL,PRIMARY KEY(tenant,id))"))
        { _ = await setup.ExecuteNonQueryAsync(); }
        async ValueTask WriteAsync(DbConnection connection, DbTransaction transaction, EdgeMutation mutation, EdgeRecord record, CancellationToken cancellationToken)
        {
            Assert.Equal(mutation.DocumentId, record.Id);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"INSERT INTO \"{fixture.Store.Options.Schema}\".business_effects VALUES(@tenant,@id,1) ON CONFLICT(tenant,id) DO UPDATE SET counter=business_effects.counter+1";
            var tenant = command.CreateParameter(); tenant.ParameterName = "tenant"; tenant.Value = mutation.Scope.Tenant; command.Parameters.Add(tenant);
            var id = command.CreateParameter(); id.ParameterName = "id"; id.Value = mutation.DocumentId; command.Parameters.Add(id);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var mutation = Upsert(scope, "1", 0, "{}"u8.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.ApplyMutationWithBusinessAsync(mutation, async (connection, transaction, write, record, token) =>
        {
            await WriteAsync(connection, transaction, write, record, token); throw new InvalidOperationException("Injected after external table effect.");
        }));
        Assert.Null(await fixture.Store.GetAsync(scope, "1")); Assert.Null(await fixture.Store.ReadChangesAsync(scope, 0));
        await using (var count = fixture.Source.CreateCommand($"SELECT count(*) FROM \"{fixture.Store.Options.Schema}\".business_effects"))
        { Assert.Equal(0L, await count.ExecuteScalarAsync()); }
        var applied = await fixture.Store.ApplyMutationWithBusinessAsync(mutation, WriteAsync);
        var reopened = new PostgreSqlEdgeServerStore(fixture.Source, fixture.Store.Options);
        var replay = await reopened.ApplyMutationWithBusinessAsync(mutation, (_, _, _, _, _) => throw new InvalidOperationException("A replay must not invoke business code."));
        Assert.Equal(applied.ServerRecord!.Revision, replay.ServerRecord!.Revision);
        var conflict = await reopened.ApplyMutationWithBusinessAsync(Upsert(scope, "1", 0, "{}"u8.ToArray()), (_, _, _, _, _) => throw new InvalidOperationException("A conflict must not invoke business code."));
        Assert.Equal(EdgeMutationOutcomeKind.Conflict, conflict.Kind);
        await using (var count = fixture.Source.CreateCommand($"SELECT counter FROM \"{fixture.Store.Options.Schema}\".business_effects"))
        { Assert.Equal(1, await count.ExecuteScalarAsync()); }
        Assert.Equal(1, (await reopened.ReadChangesAsync(scope, 0))!.ToPosition);
    });

    [Fact]
    public Task Mutation_receipt_business_effect_and_feed_are_atomic_and_persist_across_store_reopen() => WithFixtureAsync(async fixture =>
    {
        var scope = new EdgeScope("tenant", "selected-orders", 1);
        await fixture.Store.ActivateScopeAsync(scope);
        var mutation = Upsert(scope, "1", 0, "{\"count\":1}"u8.ToArray());
        var first = await fixture.Store.ApplyMutationAsync(mutation);
        var reopened = new PostgreSqlEdgeServerStore(fixture.Source, fixture.Store.Options);
        var replay = await reopened.ApplyMutationAsync(mutation);
        Assert.Equal(first.Kind, replay.Kind);
        Assert.Equal(first.ServerRecord!.Revision, replay.ServerRecord!.Revision);
        Assert.Equal(first.ServerRecord.Payload.ToArray(), replay.ServerRecord.Payload.ToArray());
        await Assert.ThrowsAsync<EdgeMutationIdentityException>(async () => await reopened.ApplyMutationAsync(new EdgeMutation(scope, mutation.Id, "other", 0, EdgeMutationKind.Upsert, "{}"u8.ToArray())));
        var batch = (await reopened.ReadChangesAsync(scope, 0))!;
        Assert.Single(batch.Records);
        Assert.Equal(1, batch.ToPosition);
    });

    [Fact]
    public Task Confirmed_receipt_releases_payload_but_permanently_rejects_late_retry() => WithFixtureAsync(async fixture =>
    {
        var scope = new EdgeScope("tenant", "orders", 1);
        await fixture.Store.ActivateScopeAsync(scope);
        await using var bounded = new PostgreSqlEdgeServerStore(fixture.Source, fixture.Store.Options with { MaxReceiptBytesPerScope = 40 });
        var mutation = Upsert(scope, "a", 0, "{\"text\":\"12345678901234567890\"}"u8.ToArray());
        var applied = await bounded.ApplyMutationAsync(mutation);
        Assert.Equal((long)mutation.Payload.Length, (await bounded.ReadHealthAsync(scope)).ReceiptBytes);
        await bounded.FinalizeMutationReceiptAsync(mutation);
        await bounded.FinalizeMutationReceiptAsync(mutation);
        var health = await bounded.ReadHealthAsync(scope);
        Assert.Equal(1, health.ReceiptCount);
        Assert.Equal(0, health.ReceiptBytes);
        Assert.Equal(1, health.ChangeCount);
        Assert.Equal(applied.ServerRecord!.Revision, (await bounded.GetAsync(scope, "a"))!.Revision);
        await Assert.ThrowsAsync<EdgeServerReceiptFinalizedException>(async () => await bounded.ApplyMutationAsync(mutation));
        await Assert.ThrowsAsync<EdgeMutationIdentityException>(async () => await bounded.ApplyMutationAsync(new EdgeMutation(scope, mutation.Id, "other", 0, EdgeMutationKind.Upsert, "{}"u8.ToArray())));
        await Assert.ThrowsAsync<EdgeMutationIdentityException>(async () => await bounded.FinalizeMutationReceiptAsync(new EdgeMutation(scope, mutation.Id, "other", 0, EdgeMutationKind.Upsert, "{}"u8.ToArray())));
        await Assert.ThrowsAsync<EdgeServerReceiptMissingException>(async () => await bounded.FinalizeMutationReceiptAsync(Upsert(scope, "missing", 0, "{}"u8.ToArray())));
        Assert.Equal(EdgeMutationOutcomeKind.Applied, (await bounded.ApplyMutationAsync(Upsert(scope, "b", 0, "{\"text\":\"12345678901234567890\"}"u8.ToArray()))).Kind);
        Assert.Equal(2, (await bounded.ReadHealthAsync(scope)).ReceiptCount);
        Assert.Equal(2, (await bounded.ReadChangesAsync(scope, 0))!.ToPosition);
    }, new EdgeServerOptions { MaxRecordBytes = 40 });

    [Fact]
    public Task Ordered_confirmed_prefix_reclaims_receipts_and_persistently_fences_late_retries() => WithFixtureAsync(async fixture =>
    {
        var scope = new EdgeScope("tenant", "orders", 1);
        await fixture.Store.ActivateScopeAsync(scope);
        await using var bounded = new PostgreSqlEdgeServerStore(fixture.Source, fixture.Store.Options with { MaxReceiptsPerScope = 2 });
        var stream = EdgeOrderedMutationId.NewStreamId();
        EdgeMutation Write(long sequence) => new(scope, EdgeOrderedMutationId.Create(stream, sequence), sequence.ToString(), 0, EdgeMutationKind.Upsert, "{}"u8.ToArray());
        var first = Write(1); var second = Write(2); var third = Write(3);
        Assert.Equal((stream, 2L), Parse(second.Id));
        await Assert.ThrowsAsync<EdgeMutationIdentityException>(async () => await bounded.ApplyMutationAsync(second));
        _ = await bounded.ApplyMutationAsync(first);
        _ = await bounded.ApplyMutationAsync(second);
        await Assert.ThrowsAsync<EdgeMutationIdentityException>(async () => await bounded.ApplyMutationAsync(Write(4)));
        await Assert.ThrowsAsync<EdgeCapacityException>(async () => await bounded.ApplyMutationAsync(third));
        await bounded.FinalizeMutationReceiptAsync(first);
        await Assert.ThrowsAsync<EdgeRevisionConflictException>(async () => await bounded.AdvanceOrderedReceiptHorizonAsync(scope, second.Id));
        var reclaim = bounded.AdvanceOrderedReceiptHorizonAsync(scope, first.Id).AsTask();
        var concurrentRetries = Enumerable.Range(0, 8).Select(async _ =>
        { await Assert.ThrowsAsync<EdgeServerReceiptFinalizedException>(async () => await bounded.ApplyMutationAsync(first)); }).ToArray();
        await Task.WhenAll(concurrentRetries.Append(reclaim));
        Assert.Equal(1, await reclaim);
        Assert.Equal(1, (await bounded.ReadHealthAsync(scope)).ReceiptCount);
        await Assert.ThrowsAsync<EdgeServerReceiptFinalizedException>(async () => await bounded.FinalizeMutationReceiptAsync(first));
        await Assert.ThrowsAsync<EdgeServerReceiptFinalizedException>(async () => await bounded.FinalizeMutationReceiptAsync(new EdgeMutation(scope, first.Id, "other", 0, EdgeMutationKind.Upsert, "{}"u8.ToArray())));
        await Assert.ThrowsAsync<EdgeServerReceiptFinalizedException>(async () => await bounded.ApplyMutationAsync(first));
        await bounded.FinalizeMutationReceiptAsync(second);
        Assert.Equal(1, await bounded.AdvanceOrderedReceiptHorizonAsync(scope, second.Id));
        Assert.Equal(0, await bounded.AdvanceOrderedReceiptHorizonAsync(scope, second.Id));
        Assert.Equal(0, (await bounded.ReadHealthAsync(scope)).ReceiptCount);
        var reopened = new PostgreSqlEdgeServerStore(fixture.Source, bounded.Options);
        await Assert.ThrowsAsync<EdgeServerReceiptFinalizedException>(async () => await reopened.ApplyMutationAsync(first));
        await Assert.ThrowsAsync<EdgeServerReceiptFinalizedException>(async () => await reopened.ApplyMutationAsync(new EdgeMutation(scope, first.Id, "other", 0, EdgeMutationKind.Upsert, "{}"u8.ToArray())));
        Assert.Equal(EdgeMutationOutcomeKind.Applied, (await reopened.ApplyMutationAsync(third)).Kind);
        Assert.Equal(1, (await reopened.ReadHealthAsync(scope)).ReceiptCount);
        var rotated = new EdgeScope(scope.Tenant, scope.Id, 2);
        await reopened.ActivateScopeAsync(rotated);
        await Assert.ThrowsAsync<EdgeScopeMismatchException>(async () => await reopened.ApplyMutationAsync(first));
        Assert.Equal(EdgeMutationOutcomeKind.Applied, (await reopened.ApplyMutationAsync(new EdgeMutation(rotated,
            EdgeOrderedMutationId.Create(stream, 1), "fresh", 0, EdgeMutationKind.Upsert, "{}"u8.ToArray()))).Kind);
    });

    private static (string Stream, long Sequence) Parse(Guid id)
    {
        Assert.True(EdgeOrderedMutationId.TryParse(id, out var stream, out var sequence));
        return (stream, sequence);
    }

    [Fact]
    public Task Version_two_receipts_upgrade_to_unconfirmed_version_four() => WithFixtureAsync(async fixture =>
    {
        var scope = new EdgeScope("tenant", "orders", 1);
        await fixture.Store.ActivateScopeAsync(scope);
        var mutation = Upsert(scope, "a", 0, "{}"u8.ToArray());
        var first = await fixture.Store.ApplyMutationAsync(mutation);
        await using (var downgrade = fixture.Source.CreateCommand($"ALTER TABLE \"{fixture.Store.Options.Schema}\".receipts DROP COLUMN finalized; UPDATE \"{fixture.Store.Options.Schema}\".metadata SET version=2"))
        { _ = await downgrade.ExecuteNonQueryAsync(); }
        await fixture.Store.InitializeAsync();
        Assert.Equal(first.ServerRecord!.Revision, (await fixture.Store.ApplyMutationAsync(mutation)).ServerRecord!.Revision);
        await fixture.Store.FinalizeMutationReceiptAsync(mutation);
        await Assert.ThrowsAsync<EdgeServerReceiptFinalizedException>(async () => await fixture.Store.ApplyMutationAsync(mutation));
    });

    [Fact]
    public Task Competing_CAS_has_one_applied_result_and_conflicts_preserve_authoritative_revision() => WithFixtureAsync(async fixture =>
    {
        var scope = new EdgeScope("tenant", "orders", 1);
        await fixture.Store.ActivateScopeAsync(scope);
        var initial = await fixture.Store.ApplyMutationAsync(Upsert(scope, "1", 0, "{}"u8.ToArray()));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Store.ApplyMutationAsync(Upsert(scope, "1", initial.ServerRecord!.Revision, "{\"new\":true}"u8.ToArray())).AsTask()));
        Assert.Single(outcomes, static outcome => outcome.Kind is EdgeMutationOutcomeKind.Applied);
        Assert.Equal(7, outcomes.Count(static outcome => outcome.Kind is EdgeMutationOutcomeKind.Conflict));
        Assert.All(outcomes, outcome => Assert.True(outcome.ServerRecord!.Revision > initial.ServerRecord!.Revision));
        Assert.Equal(2, (await fixture.Store.ReadChangesAsync(scope, 0))!.Records.Count);
    });

    [Fact]
    public Task Consistent_snapshot_copies_one_feed_boundary_and_later_changes_have_no_gap() => WithFixtureAsync(async fixture =>
    {
        var scope = new EdgeScope("tenant", "selected", 1);
        await fixture.Store.ActivateScopeAsync(scope);
        _ = await fixture.Store.ApplyMutationAsync(Upsert(scope, "a", 0, "{\"before\":true}"u8.ToArray()));
        var snapshot = await fixture.Store.BeginSnapshotAsync(scope);
        _ = await fixture.Store.ApplyMutationAsync(Upsert(scope, "b", 0, "{\"after\":true}"u8.ToArray()));
        var page = await fixture.Store.ReadSnapshotPageAsync(scope, snapshot.Id);
        Assert.Equal("a", Assert.Single(page.Records).Id);
        var changes = (await fixture.Store.ReadChangesAsync(scope, snapshot.Position))!;
        Assert.Equal("b", Assert.Single(changes.Records).Id);
        Assert.Equal(snapshot.Position, changes.FromPosition);
        await fixture.Store.ReleaseSnapshotAsync(scope, snapshot.Id);
        await Assert.ThrowsAsync<EdgeServerSnapshotExpiredException>(async () => await fixture.Store.ReadSnapshotPageAsync(scope, snapshot.Id));
    });

    [Fact]
    public Task Inbox_insert_failure_rolls_back_record_feed_checkpoint_and_admission_counters() => WithFixtureAsync(async fixture =>
    {
        var scope = new EdgeScope("tenant", "orders", 1);
        await fixture.Store.ActivateScopeAsync(scope);
        await using (var inject = fixture.Source.CreateCommand($"""
            CREATE FUNCTION "{fixture.Store.Options.Schema}".fail_receipt() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected receipt failure'; END $$;
            CREATE TRIGGER fail_receipt BEFORE INSERT ON "{fixture.Store.Options.Schema}".receipts FOR EACH ROW EXECUTE FUNCTION "{fixture.Store.Options.Schema}".fail_receipt()
            """)) { _ = await inject.ExecuteNonQueryAsync(); }
        var mutation = Upsert(scope, "1", 0, "{}"u8.ToArray());
        await Assert.ThrowsAnyAsync<DbException>(async () => await fixture.Store.ApplyMutationAsync(mutation));
        Assert.Null(await fixture.Store.GetAsync(scope, "1"));
        Assert.Null(await fixture.Store.ReadChangesAsync(scope, 0));
        await using (var remove = fixture.Source.CreateCommand($"DROP TRIGGER fail_receipt ON \"{fixture.Store.Options.Schema}\".receipts")) { _ = await remove.ExecuteNonQueryAsync(); }
        var applied = await fixture.Store.ApplyMutationAsync(mutation);
        Assert.Equal(EdgeMutationOutcomeKind.Applied, applied.Kind);
        Assert.Equal(1, (await fixture.Store.ReadChangesAsync(scope, 0))!.ToPosition);
    });

    [Fact]
    public Task Tenant_scope_epoch_tombstones_and_explicit_retention_floor_are_fenced() => WithFixtureAsync(async fixture =>
    {
        var scope = new EdgeScope("tenant", "orders", 1);
        var other = new EdgeScope("other", "orders", 1);
        await fixture.Store.ActivateScopeAsync(scope);
        await fixture.Store.ActivateScopeAsync(other);
        var applied = await fixture.Store.ApplyMutationAsync(Upsert(scope, "1", 0, "{}"u8.ToArray()));
        Assert.Null(await fixture.Store.GetAsync(other, "1"));
        var removed = await fixture.Store.ApplyMutationAsync(new EdgeMutation(scope, Guid.NewGuid(), "1", applied.ServerRecord!.Revision, EdgeMutationKind.Delete, default));
        Assert.True(removed.ServerRecord!.Deleted);
        var resurrect = await fixture.Store.ApplyMutationAsync(Upsert(scope, "1", 0, "{}"u8.ToArray()));
        Assert.Equal(EdgeMutationOutcomeKind.Conflict, resurrect.Kind);
        Assert.True(resurrect.ServerRecord!.Deleted);
        Assert.Equal(1, await fixture.Store.PruneChangesAsync(scope, 2, 1));
        await Assert.ThrowsAsync<EdgeServerReplayExpiredException>(async () => await fixture.Store.ReadChangesAsync(scope, 0));
        Assert.Equal(2, (await fixture.Store.ReadChangesAsync(scope, 1))!.ToPosition);
        await fixture.Store.ActivateScopeAsync(new EdgeScope("tenant", "orders", 2));
        await Assert.ThrowsAsync<EdgeScopeMismatchException>(async () => await fixture.Store.GetAsync(scope, "1"));
        Assert.Null(await fixture.Store.GetAsync(new EdgeScope("tenant", "orders", 2), "1"));
    });

    [Fact]
    public Task Bounded_bytes_rows_snapshots_and_receipts_reject_complete_transitions() => WithFixtureAsync(async fixture =>
    {
        var scope = new EdgeScope("tenant", "orders", 1);
        await fixture.Store.ActivateScopeAsync(scope);
        await using var bounded = new PostgreSqlEdgeServerStore(fixture.Source, fixture.Store.Options with
        {
            MaxRecordsPerScope = 2,
            MaxReceiptsPerScope = 2,
            MaxSnapshotsPerScope = 1,
            MaxBatchBytes = 40,
            MaxBatchRecords = 10,
        });
        _ = await bounded.ApplyMutationAsync(Upsert(scope, "a", 0, "{\"text\":\"12345678901234567890\"}"u8.ToArray()));
        _ = await bounded.ApplyMutationAsync(Upsert(scope, "b", 0, "{\"text\":\"12345678901234567890\"}"u8.ToArray()));
        await Assert.ThrowsAsync<EdgeCapacityException>(async () => await bounded.ApplyMutationAsync(Upsert(scope, "c", 0, "{}"u8.ToArray())));
        Assert.Null(await bounded.GetAsync(scope, "c"));
        var snapshot = await bounded.BeginSnapshotAsync(scope);
        await Assert.ThrowsAsync<EdgeCapacityException>(async () => await bounded.BeginSnapshotAsync(scope));
        Assert.Single((await bounded.ReadSnapshotPageAsync(scope, snapshot.Id, 10)).Records);
        var first = (await bounded.ReadChangesAsync(scope, 0, 10))!;
        Assert.Single(first.Records);
        Assert.Equal(1, first.ToPosition);
    }, new EdgeServerOptions { MaxRecordBytes = 40 });

    [Fact]
    public Task Receipt_payload_bytes_are_bounded_and_replay_survives_admission_refusal() => WithFixtureAsync(async fixture =>
    {
        var scope = new EdgeScope("tenant", "orders", 1);
        await fixture.Store.ActivateScopeAsync(scope);
        await using var bounded = new PostgreSqlEdgeServerStore(fixture.Source, fixture.Store.Options with
        {
            MaxReceiptBytesPerScope = 40,
            MaxReceiptsPerScope = 10,
        });
        var mutation = Upsert(scope, "a", 0, "{\"text\":\"12345678901234567890\"}"u8.ToArray());
        var applied = await bounded.ApplyMutationAsync(mutation);
        Assert.Equal((long)mutation.Payload.Length, (await bounded.ReadHealthAsync(scope)).ReceiptBytes);
        await using (var oldSchema = fixture.Source.CreateCommand($"ALTER TABLE \"{fixture.Store.Options.Schema}\".scopes DROP COLUMN receipt_bytes CASCADE; UPDATE \"{fixture.Store.Options.Schema}\".metadata SET version=1"))
        { _ = await oldSchema.ExecuteNonQueryAsync(); }
        await bounded.InitializeAsync();
        Assert.Equal((long)mutation.Payload.Length, (await bounded.ReadHealthAsync(scope)).ReceiptBytes);
        var conflict = Upsert(scope, "a", 0, "{}"u8.ToArray());
        await Assert.ThrowsAsync<EdgeCapacityException>(async () => await bounded.ApplyMutationAsync(conflict));
        Assert.Equal(1, (await bounded.ReadHealthAsync(scope)).ReceiptCount);
        Assert.Equal((long)mutation.Payload.Length, (await bounded.ReadHealthAsync(scope)).ReceiptBytes);
        Assert.Equal(applied.ServerRecord!.Revision, (await bounded.ApplyMutationAsync(mutation)).ServerRecord!.Revision);
        Assert.Equal(1, (await bounded.ReadHealthAsync(scope)).ReceiptCount);
    }, new EdgeServerOptions { MaxRecordBytes = 40 });

    [Fact]
    public Task Bounded_four_writer_workload_has_contiguous_feed_and_idempotent_receipts() => WithFixtureAsync(async fixture =>
    {
        var scope = new EdgeScope("tenant", "orders", 1);
        await fixture.Store.ActivateScopeAsync(scope);
        await Parallel.ForEachAsync(Enumerable.Range(0, 128), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, token) =>
        {
            var mutation = Upsert(scope, i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), 0, System.Text.Encoding.UTF8.GetBytes("{\"data\":\"" + new string('x', 1024) + "\"}"));
            var result = await fixture.Store.ApplyMutationAsync(mutation, token);
            var replay = await fixture.Store.ApplyMutationAsync(mutation, token);
            Assert.Equal(result.ServerRecord!.Revision, replay.ServerRecord!.Revision);
        });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long position = 0;
        while (await fixture.Store.ReadChangesAsync(scope, position, 17) is { } batch)
        {
            Assert.Equal(position + batch.Records.Count, batch.ToPosition);
            Assert.All(batch.Records, record => Assert.True(seen.Add(record.Id)));
            position = batch.ToPosition;
        }
        Assert.Equal(128, position);
        Assert.Equal(128, seen.Count);
    });

    private static EdgeMutation Upsert(EdgeScope scope, string id, long expected, byte[] payload) => new(scope, Guid.NewGuid(), id, expected, EdgeMutationKind.Upsert, payload);
    private static async Task WithFixtureAsync(Func<Fixture, Task> test, EdgeServerOptions? options = null)
    {
        var connection = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure the disposable PostgreSQL Edge server fixture.");
        await using var source = BlueTuskDataSource.Create(connection);
        var schema = "edge_server_" + Guid.NewGuid().ToString("N");
        await using var store = new PostgreSqlEdgeServerStore(source, (options ?? new EdgeServerOptions()) with { Schema = schema });
        try { await store.InitializeAsync(); await test(new(source, store)); }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            _ = await cleanup.ExecuteNonQueryAsync();
        }
    }
    private sealed record Fixture(BlueTuskDataSource Source, PostgreSqlEdgeServerStore Store);
}

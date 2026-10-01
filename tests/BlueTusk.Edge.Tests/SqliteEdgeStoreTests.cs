using System.Globalization;
using System.Text;
using BlueTusk.Edge.Sqlite;
using Microsoft.Data.Sqlite;

namespace BlueTusk.Edge.Tests;

public sealed class SqliteEdgeStoreTests
{
    private static readonly EdgeScope Scope = new("tenant", "warehouse-readers", 1);

    [Fact]
    public Task Snapshot_rows_are_staged_until_atomic_cutover_and_resume_after_reopen() => WithStoreAsync(async (store, options) =>
    {
        var snapshot = new EdgeSnapshot(Guid.NewGuid(), 10);
        await store.BeginSnapshotAsync(Scope, snapshot);
        await store.ApplySnapshotBatchAsync(Scope, snapshot.Id, [Record("1", 1, "old")]);
        Assert.Null(await store.GetAsync(Scope, "1"));
        Assert.False((await store.GetCheckpointAsync(Scope)).SnapshotReady);
        var reopened = new SqliteEdgeStore(options);
        await reopened.InitializeAsync();
        await reopened.BeginSnapshotAsync(Scope, snapshot);
        await reopened.ApplySnapshotBatchAsync(Scope, snapshot.Id, [Record("2", 2, "second")]);
        await reopened.CommitSnapshotAsync(Scope, snapshot.Id);
        Assert.Equal(new EdgeCheckpoint(10, true), await reopened.GetCheckpointAsync(Scope));
        Assert.Equal("{\"value\":\"old\"}", Json((await reopened.GetAsync(Scope, "1"))!.Payload));
        var refresh = new EdgeSnapshot(Guid.NewGuid(), 20);
        await reopened.BeginSnapshotAsync(Scope, refresh);
        await reopened.ApplySnapshotBatchAsync(Scope, refresh.Id, [Record("1", 3, "new")]);
        Assert.Equal("{\"value\":\"old\"}", Json((await reopened.GetAsync(Scope, "1"))!.Payload));
        await reopened.CommitSnapshotAsync(Scope, refresh.Id);
        Assert.Equal("{\"value\":\"new\"}", Json((await reopened.GetAsync(Scope, "1"))!.Payload));
        Assert.Null(await reopened.GetAsync(Scope, "2"));
    });

    [Fact]
    public Task Changes_revision_fences_and_checkpoints_roll_back_whole_conflicting_batches() => WithReadyStoreAsync(async (store, unusedOptions) =>
    {
        await store.ApplyChangesAsync(Scope, new EdgeChangeBatch(0, 1, [Record("1", 1, "one")]));
        _ = await Assert.ThrowsAsync<EdgeRevisionConflictException>(() => store.ApplyChangesAsync(Scope, new EdgeChangeBatch(1, 2, [Record("2", 1, "must roll back"), Record("1", 1, "different at same revision")])).AsTask());
        Assert.Null(await store.GetAsync(Scope, "2"));
        Assert.Equal(1, (await store.GetCheckpointAsync(Scope)).Position);
        _ = await Assert.ThrowsAsync<EdgeCheckpointMismatchException>(() => store.ApplyChangesAsync(Scope, new EdgeChangeBatch(0, 3, [])).AsTask());
        await store.ApplyChangesAsync(Scope, new EdgeChangeBatch(1, 2, [new EdgeRecord("1", 2, ReadOnlyMemory<byte>.Empty, true)]));
        await store.ApplyChangesAsync(Scope, new EdgeChangeBatch(2, 3, [Record("1", 1, "stale resurrection")]));
        Assert.True((await store.GetAsync(Scope, "1"))!.Deleted);
        Assert.Empty((await store.ReadPageAsync(Scope)).Items);
    });

    [Fact]
    public Task Mutation_identity_queue_and_cache_overlay_survive_reopen_and_duplicate_acknowledgement() => WithReadyStoreAsync(async (store, options) =>
    {
        var mutation = Mutation("1", 0, "offline");
        await store.EnqueueAsync(mutation);
        await store.EnqueueAsync(mutation);
        var offline = (await store.GetAsync(Scope, "1"))!;
        Assert.Equal(mutation.Id, offline.PendingMutationId);
        Assert.Equal(0, offline.ServerRevision);
        Assert.Equal("{\"value\":\"offline\"}", Json(offline.Payload));
        var reopened = new SqliteEdgeStore(options);
        var lease = (await reopened.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        Assert.Equal(mutation.Id, lease.Mutation.Id);
        var applied = new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("1", 1, "normalized"));
        await reopened.AcknowledgeAsync(lease, applied);
        await reopened.AcknowledgeAsync(lease, applied);
        await reopened.EnqueueAsync(mutation);
        Assert.Null(await reopened.ClaimAsync(Scope, TimeSpan.FromMinutes(1)));
        var committed = (await reopened.GetAsync(Scope, "1"))!;
        Assert.Null(committed.PendingMutationId);
        Assert.Equal(1, committed.ServerRevision);
        Assert.Equal("{\"value\":\"normalized\"}", Json(committed.Payload));
        _ = await Assert.ThrowsAsync<EdgeMutationIdentityException>(() => reopened.EnqueueAsync(new EdgeMutation(Scope, mutation.Id, "1", 0, EdgeMutationKind.Upsert, Payload("different"))).AsTask());
    });

    [Fact]
    public Task Ordered_allocation_and_confirmation_outbox_are_atomic_across_instances_and_restart() => WithReadyStoreAsync(async (store, options) =>
    {
        var allocated = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
            await new SqliteEdgeStore(options).EnqueueOrderedAsync(Scope, index.ToString("D3", CultureInfo.InvariantCulture), 0, EdgeMutationKind.Upsert, Payload("offline")))));
        var sequences = allocated.Select(mutation =>
        {
            Assert.True(EdgeOrderedMutationId.TryParse(mutation.Id, out var stream, out var sequence));
            return (stream, sequence);
        }).ToArray();
        Assert.Single(sequences.Select(static value => value.stream).Distinct());
        Assert.Equal(Enumerable.Range(1, 8).Select(static value => (long)value), sequences.Select(static value => value.sequence).Order());
        var first = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        Assert.True(EdgeOrderedMutationId.TryParse(first.Mutation.Id, out _, out var firstSequence) && firstSequence == 1);
        Assert.Null(await new SqliteEdgeStore(options).ClaimAsync(Scope, TimeSpan.FromMinutes(1)));
        await store.AcknowledgeAsync(first, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record(first.Mutation.DocumentId, 1, "server")));
        var reopened = new SqliteEdgeStore(options);
        var confirmation = (await reopened.ReadNextUnconfirmedOrderedReceiptAsync(Scope))!;
        Assert.Equal(first.Mutation.Id, confirmation.Id);
        Assert.Equal(first.Mutation.Fingerprint, confirmation.Fingerprint);
        await reopened.MarkOrderedReceiptConfirmedAsync(confirmation);
        Assert.Null(await reopened.ReadNextUnconfirmedOrderedReceiptAsync(Scope));
        Assert.Equal(first.Mutation.Id, await reopened.ReadConfirmedOrderedHorizonAsync(Scope));
        await new SqliteEdgeStore(options).MarkOrderedReceiptHorizonAsync(Scope, first.Mutation.Id);
        await reopened.MarkOrderedReceiptHorizonAsync(Scope, first.Mutation.Id);
        Assert.Null(await reopened.ReadConfirmedOrderedHorizonAsync(Scope));
        var second = (await reopened.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        Assert.True(EdgeOrderedMutationId.TryParse(second.Mutation.Id, out _, out var nextSequence) && nextSequence == 2);
    });

    [Fact]
    public Task Ordered_outbox_insert_failure_rolls_back_authoritative_acknowledgement() => WithReadyStoreAsync(async (store, options) =>
    {
        _ = await store.EnqueueOrderedAsync(Scope, "ordered", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var lease = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_ordered_outbox BEFORE INSERT ON ordered_confirmations BEGIN SELECT RAISE(ABORT,'injected outbox failure'); END";
        _ = await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(async () => await store.AcknowledgeAsync(lease,
            new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("ordered", 1, "server"))));
        Assert.Equal(lease.Mutation.Id, (await store.GetAsync(Scope, "ordered"))!.PendingMutationId);
        Assert.Null(await store.ReadNextUnconfirmedOrderedReceiptAsync(Scope));
        command.CommandText = "DROP TRIGGER fail_ordered_outbox"; _ = await command.ExecuteNonQueryAsync();
        await store.AcknowledgeAsync(lease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("ordered", 1, "server")));
        Assert.Equal(lease.Mutation.Id, (await store.ReadNextUnconfirmedOrderedReceiptAsync(Scope))!.Id);
    });

    [Fact]
    public Task Chained_acknowledgement_and_next_claim_commit_atomically() => WithReadyStoreAsync(async (store, options) =>
    {
        var first = await store.EnqueueOrderedAsync(Scope, "first", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var second = await store.EnqueueOrderedAsync(Scope, "second", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var lease = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        Assert.Equal(first.Id, lease.Mutation.Id);
        var outcome = new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("first", 1, "server"));

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_next_claim BEFORE UPDATE OF status ON mutations WHEN NEW.document_id='second' BEGIN SELECT RAISE(ABORT,'injected claim failure'); END";
        _ = await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(async () => await store.AcknowledgeAndClaimNextAsync(lease, outcome, TimeSpan.FromMinutes(1)));
        Assert.Equal(first.Id, (await store.GetAsync(Scope, "first"))!.PendingMutationId);
        Assert.Null(await store.ReadNextUnconfirmedOrderedReceiptAsync(Scope));

        command.CommandText = "DROP TRIGGER fail_next_claim"; _ = await command.ExecuteNonQueryAsync();
        var reopened = new SqliteEdgeStore(options);
        var next = (await reopened.AcknowledgeAndClaimNextAsync(lease, outcome, TimeSpan.FromMinutes(1)))!;
        Assert.Equal(second.Id, next.Mutation.Id);
        Assert.Null((await reopened.GetAsync(Scope, "first"))!.PendingMutationId);
        Assert.Equal(first.Id, (await reopened.ReadNextUnconfirmedOrderedReceiptAsync(Scope))!.Id);
        Assert.Null(await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)));
        await reopened.AcknowledgeAsync(next, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("second", 1, "server")));
    });

    [Fact]
    public Task Ordered_batch_claims_once_and_acknowledges_atomically() => WithReadyStoreAsync(async (store, options) =>
    {
        var first = await store.EnqueueOrderedAsync(Scope, "first", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var second = await store.EnqueueOrderedAsync(Scope, "second", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var leases = (await store.ClaimOrderedBatchAsync(Scope, 2, TimeSpan.FromMinutes(1)))!;
        Assert.Equal(new[] { first.Id, second.Id }, leases.Select(static lease => lease.Mutation.Id).ToArray());
        Assert.Null(await new SqliteEdgeStore(options).ClaimAsync(Scope, TimeSpan.FromMinutes(1)));
        await store.AcknowledgeBatchAsync(leases.Select(lease => new EdgeMutationAcknowledgement(lease,
            new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record(lease.Mutation.DocumentId, 1, "server")))).ToArray());
        Assert.Null((await store.GetAsync(Scope, "first"))!.PendingMutationId);
        Assert.Null((await store.GetAsync(Scope, "second"))!.PendingMutationId);
        Assert.Equal(first.Id, (await store.ReadNextUnconfirmedOrderedReceiptAsync(Scope))!.Id);
    });

    [Fact]
    public Task Ordered_batch_acknowledgement_rolls_back_all_outcomes_when_one_outbox_write_fails() => WithReadyStoreAsync(async (store, options) =>
    {
        var first = await store.EnqueueOrderedAsync(Scope, "first", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var second = await store.EnqueueOrderedAsync(Scope, "second", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var leases = (await store.ClaimOrderedBatchAsync(Scope, 2, TimeSpan.FromMinutes(1)))!;
        var outcomes = leases.Select(lease => new EdgeMutationAcknowledgement(lease,
            new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record(lease.Mutation.DocumentId, 1, "server")))).ToArray();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_second_batch_outbox BEFORE INSERT ON ordered_confirmations WHEN NEW.document_id='second' BEGIN SELECT RAISE(ABORT,'injected batch failure'); END";
        _ = await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(async () => await store.AcknowledgeBatchAsync(outcomes));
        Assert.Equal(first.Id, (await store.GetAsync(Scope, "first"))!.PendingMutationId);
        Assert.Equal(second.Id, (await store.GetAsync(Scope, "second"))!.PendingMutationId);
        Assert.Null(await store.ReadNextUnconfirmedOrderedReceiptAsync(Scope));
        command.CommandText = "DROP TRIGGER fail_second_batch_outbox"; _ = await command.ExecuteNonQueryAsync();
        await store.AcknowledgeBatchAsync(outcomes);
        Assert.Null((await store.GetAsync(Scope, "first"))!.PendingMutationId);
        Assert.Null((await store.GetAsync(Scope, "second"))!.PendingMutationId);
    });

    [Fact]
    public Task Releasing_an_unprocessed_batch_suffix_fences_its_old_lease() => WithReadyStoreAsync(async (store, unusedOptions) =>
    {
        _ = await store.EnqueueOrderedAsync(Scope, "first", 0, EdgeMutationKind.Upsert, Payload("offline"));
        _ = await store.EnqueueOrderedAsync(Scope, "second", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var leases = (await store.ClaimOrderedBatchAsync(Scope, 2, TimeSpan.FromMinutes(1)))!;
        await store.AcknowledgeBatchAsync([new EdgeMutationAcknowledgement(leases[0],
            new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("first", 1, "server")))]);
        await store.ReleaseOrderedBatchAsync([leases[1]]);
        await Assert.ThrowsAsync<EdgeLeaseLostException>(async () => await store.AcknowledgeAsync(leases[1],
            new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("second", 1, "server"))));
        var retried = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        Assert.Equal(leases[1].Mutation.Id, retried.Mutation.Id);
        Assert.True(retried.Fence > leases[1].Fence);
    });

    [Fact]
    public Task Ordered_batch_coordinator_persists_successful_prefix_and_releases_failed_suffix() => WithReadyStoreAsync(async (store, unusedOptions) =>
    {
        var first = await store.EnqueueOrderedAsync(Scope, "first", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var second = await store.EnqueueOrderedAsync(Scope, "second", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var remote = new FailSecondTransport();
        await Assert.ThrowsAsync<IOException>(async () => await new EdgeSynchronizationCoordinator(store, remote)
            .SynchronizeAsync(Scope, maxPushes: 2));
        Assert.Equal(2, remote.Attempts);
        Assert.Null((await store.GetAsync(Scope, "first"))!.PendingMutationId);
        Assert.Equal(first.Id, (await store.ReadNextUnconfirmedOrderedReceiptAsync(Scope))!.Id);
        Assert.Equal(second.Id, (await store.GetAsync(Scope, "second"))!.PendingMutationId);
        Assert.Equal(EdgeMutationStatus.Pending, (await store.GetAsync(Scope, "second"))!.PendingStatus);
        var retry = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        Assert.Equal(second.Id, retry.Mutation.Id);
    });

    [Fact]
    public Task Coordinator_does_not_claim_beyond_its_push_limit() => WithReadyStoreAsync(async (store, unusedOptions) =>
    {
        await store.EnqueueAsync(Mutation("first", 0, "offline"));
        await store.EnqueueAsync(Mutation("second", 0, "offline"));
        var remote = new SuccessfulTransport();
        await new EdgeSynchronizationCoordinator(store, remote).SynchronizeAsync(Scope, maxPushes: 1);
        Assert.Equal(1, remote.Applies);
        var remaining = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        Assert.Equal("second", remaining.Mutation.DocumentId);
    });

    [Fact]
    public Task Ordered_receipt_batch_marks_only_a_verified_atomic_prefix() => WithReadyStoreAsync(async (store, options) =>
    {
        var first = await store.EnqueueOrderedAsync(Scope, "first", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var firstLease = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        await store.AcknowledgeAsync(firstLease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("first", 1, "server")));
        var second = await store.EnqueueOrderedAsync(Scope, "second", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var secondLease = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        await store.AcknowledgeAsync(secondLease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("second", 1, "server")));

        Assert.Equal([first.Id], (await store.ReadUnconfirmedOrderedReceiptsAsync(Scope, 1)).Select(static mutation => mutation.Id));
        Assert.Equal([first.Id, second.Id], (await store.ReadUnconfirmedOrderedReceiptsAsync(Scope, 2)).Select(static mutation => mutation.Id));
        var altered = new EdgeMutation(Scope, second.Id, second.DocumentId, second.ExpectedRevision, second.Kind, Payload("changed"));
        await Assert.ThrowsAsync<EdgeMutationIdentityException>(async () =>
            await store.MarkOrderedReceiptsConfirmedAsync(Scope, [first, altered]));
        Assert.Equal(first.Id, (await store.ReadNextUnconfirmedOrderedReceiptAsync(Scope))!.Id);
        Assert.Null(await store.ReadConfirmedOrderedHorizonAsync(Scope));

        var reopened = new SqliteEdgeStore(options);
        await reopened.MarkOrderedReceiptsConfirmedAsync(Scope, [first, second]);
        await reopened.MarkOrderedReceiptsConfirmedAsync(Scope, [first, second]);
        Assert.Null(await reopened.ReadNextUnconfirmedOrderedReceiptAsync(Scope));
        Assert.Equal(second.Id, await reopened.ReadConfirmedOrderedHorizonAsync(Scope, 2));
        await reopened.MarkOrderedReceiptHorizonAsync(Scope, second.Id);
        Assert.Null(await reopened.ReadConfirmedOrderedHorizonAsync(Scope));
    });

    [Fact]
    public Task Ordered_coordinator_batches_successful_confirmation_prefix_before_remote_failure() => WithReadyStoreAsync(async (store, options) =>
    {
        for (var index = 0; index < 2; index++)
        {
            var id = index == 0 ? "first" : "second";
            _ = await store.EnqueueOrderedAsync(Scope, id, 0, EdgeMutationKind.Upsert, Payload("offline"));
            var lease = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
            await store.AcknowledgeAsync(lease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record(id, 1, "server")));
        }
        var remote = new OrderedReceiptTransport { FailConfirmationAt = 2, LoseFirstHorizon = false };
        await Assert.ThrowsAsync<IOException>(async () => await new EdgeSynchronizationCoordinator(store, remote).SynchronizeAsync(Scope));
        Assert.Equal(2, remote.Confirmations);
        var reopened = new SqliteEdgeStore(options);
        var remaining = (await reopened.ReadNextUnconfirmedOrderedReceiptAsync(Scope))!;
        Assert.Equal("second", remaining.DocumentId);

        remote.FailConfirmationAt = null;
        await new EdgeSynchronizationCoordinator(reopened, remote).SynchronizeAsync(Scope);
        Assert.Equal(3, remote.Confirmations);
        Assert.Equal(1, remote.HorizonAttempts);
        Assert.Null(await reopened.ReadNextUnconfirmedOrderedReceiptAsync(Scope));
        Assert.Null(await reopened.ReadConfirmedOrderedHorizonAsync(Scope));
    });

    [Fact]
    public Task Ordered_coordinator_retries_a_lost_horizon_response_without_reconfirming_after_reopen() => WithReadyStoreAsync(async (store, options) =>
    {
        var mutation = await store.EnqueueOrderedAsync(Scope, "automatic", 0, EdgeMutationKind.Upsert, Payload("offline"));
        var remote = new OrderedReceiptTransport();
        await Assert.ThrowsAsync<IOException>(async () => await new EdgeSynchronizationCoordinator(store, remote).SynchronizeAsync(Scope));
        Assert.Equal(1, remote.Applies); Assert.Equal(1, remote.Confirmations); Assert.Equal(1, remote.HorizonAttempts);
        Assert.Null(await store.ReadNextUnconfirmedOrderedReceiptAsync(Scope));
        Assert.Equal(mutation.Id, await store.ReadConfirmedOrderedHorizonAsync(Scope));
        var reopened = new SqliteEdgeStore(options);
        await new EdgeSynchronizationCoordinator(reopened, remote).SynchronizeAsync(Scope);
        Assert.Equal(1, remote.Applies); Assert.Equal(1, remote.Confirmations); Assert.Equal(2, remote.HorizonAttempts);
        Assert.Null(await reopened.ReadConfirmedOrderedHorizonAsync(Scope));
    });

    [Fact]
    public Task Ordered_horizon_reclaims_local_receipt_capacity_without_reusing_sequence() => WithReadyStoreAsync(async (store, unusedOptions) =>
    {
        var first = await store.EnqueueOrderedAsync(Scope, "first", 0, EdgeMutationKind.Upsert, Payload("first"));
        var firstLease = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        await store.AcknowledgeAsync(firstLease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("first", 1, "server")));
        _ = await store.EnqueueOrderedAsync(Scope, "second", 0, EdgeMutationKind.Upsert, Payload("second"));
        var secondLease = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        await Assert.ThrowsAsync<EdgeCapacityException>(async () => await store.AcknowledgeAsync(secondLease,
            new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("second", 1, "server"))));
        await store.MarkOrderedReceiptConfirmedAsync(first);
        await store.MarkOrderedReceiptHorizonAsync(Scope, first.Id);
        await store.AcknowledgeAsync(secondLease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("second", 1, "server")));
        Assert.True(EdgeOrderedMutationId.TryParse(secondLease.Mutation.Id, out _, out var sequence) && sequence == 2);
    }, new SqliteEdgeOptions { DatabasePath = "placeholder", MaxReceiptRecords = 1 });

    [Fact]
    public Task Expired_lease_recovers_with_a_higher_fence_and_rejects_stale_ack() => WithReadyStoreAsync(async (store, options) =>
    {
        await store.EnqueueAsync(Mutation("1", 0, "offline"));
        var first = (await store.ClaimAsync(Scope, TimeSpan.FromSeconds(10)))!;
        var clock = Assert.IsType<ManualClock>(options.TimeProvider);
        clock.Advance(TimeSpan.FromSeconds(11));
        var restarted = new SqliteEdgeStore(options);
        var second = (await restarted.ClaimAsync(Scope, TimeSpan.FromSeconds(10)))!;
        Assert.Equal(first.Mutation.Id, second.Mutation.Id);
        Assert.True(second.Fence > first.Fence);
        var outcome = new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("1", 1, "committed"));
        _ = await Assert.ThrowsAsync<EdgeLeaseLostException>(() => store.AcknowledgeAsync(first, outcome).AsTask());
        await restarted.AcknowledgeAsync(second, outcome);
    });

    [Fact]
    public Task Competing_store_instances_claim_each_mutation_once() => WithReadyStoreAsync(async (store, options) =>
    {
        for (var i = 0; i < 8; i++)
        {
            await store.EnqueueAsync(Mutation($"{i:D3}", 0, "offline"));
        }

        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await new SqliteEdgeStore(options).ClaimAsync(Scope, TimeSpan.FromMinutes(1)))));
        Assert.Equal(8, leases.Select(static x => x!.Mutation.Id).Distinct().Count());
        Assert.All(leases, static x => Assert.NotNull(x));
        Assert.Null(await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)));
    });

    [Fact]
    public Task Explicit_conflicts_preserve_local_content_and_resolve_with_a_new_id_atomically() => WithReadyStoreAsync(async (store, unusedOptions) =>
    {
        await store.ApplyChangesAsync(Scope, new EdgeChangeBatch(0, 1, [Record("1", 1, "server")]));
        var mutation = Mutation("1", 1, "local");
        await store.EnqueueAsync(mutation);
        var lease = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        await store.AcknowledgeAsync(lease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Conflict, Record("1", 2, "competing")));
        var conflicted = (await store.GetAsync(Scope, "1"))!;
        Assert.Equal(EdgeMutationStatus.Conflict, conflicted.PendingStatus);
        Assert.Equal(2, conflicted.ServerRevision);
        Assert.Equal("{\"value\":\"local\"}", Json(conflicted.Payload));
        Assert.Null(await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)));
        var merged = Mutation("1", 2, "merged");
        await store.ResolveConflictAsync(mutation.Id, merged);
        await store.ResolveConflictAsync(mutation.Id, merged);
        var resolvedLease = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        Assert.Equal(merged.Id, resolvedLease.Mutation.Id);
        await store.AcknowledgeAsync(resolvedLease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("1", 3, "merged")));
        Assert.Null((await store.GetAsync(Scope, "1"))!.PendingMutationId);
    });

    [Fact]
    public Task Tenant_scope_and_epoch_boundaries_purge_old_data_only_under_explicit_pending_policy() => WithReadyStoreAsync(async (store, unusedOptions) =>
    {
        await store.ApplyChangesAsync(Scope, new EdgeChangeBatch(0, 1, [Record("1", 1, "secret")]));
        var other = new EdgeScope("other", Scope.Id, 1);
        await store.ActivateScopeAsync(other);
        await EmptySnapshotAsync(store, other);
        Assert.Null(await store.GetAsync(other, "1"));
        await store.EnqueueAsync(Mutation("1", 1, "pending"));
        var epoch = new EdgeScope(Scope.Tenant, Scope.Id, 2);
        _ = await Assert.ThrowsAsync<EdgeRevisionConflictException>(() => store.ActivateScopeAsync(epoch).AsTask());
        await store.ActivateScopeAsync(epoch, EdgeEpochChangePolicy.DiscardPending);
        _ = await Assert.ThrowsAsync<EdgeScopeMismatchException>(() => store.GetAsync(Scope, "1").AsTask());
        Assert.Null(await store.GetAsync(epoch, "1"));
        Assert.False((await store.GetCheckpointAsync(epoch)).SnapshotReady);
        _ = await Assert.ThrowsAsync<EdgeScopeMismatchException>(() => store.ActivateScopeAsync(Scope).AsTask());
    });

    [Fact]
    public Task Queue_cache_batch_and_page_admission_are_bounded_and_rejected_writes_are_atomic() => WithReadyStoreAsync(async (store, unusedOptions) =>
    {
        await store.EnqueueAsync(Mutation("pending", 0, "first"));
        _ = await Assert.ThrowsAsync<EdgeCapacityException>(() => store.EnqueueAsync(Mutation("extra", 0, "second")).AsTask());
        _ = await Assert.ThrowsAsync<EdgeCapacityException>(() => store.ApplyChangesAsync(Scope, new EdgeChangeBatch(0, 1, [Record("1", 1, "one"), Record("2", 1, "two"), Record("3", 1, "three")])).AsTask());
        Assert.Equal(0, (await store.GetCheckpointAsync(Scope)).Position);
        Assert.Null(await store.GetAsync(Scope, "1"));
    }, new SqliteEdgeOptions { DatabasePath = "placeholder", MaxPendingMutations = 1, MaxCacheRecords = 2 });

    [Fact]
    public Task Delete_overlays_hide_documents_and_authoritative_ack_persists_tombstones() => WithReadyStoreAsync(async (store, unusedOptions) =>
    {
        await store.ApplyChangesAsync(Scope, new EdgeChangeBatch(0, 1, [Record("1", 1, "existing")]));
        var deletion = new EdgeMutation(Scope, Guid.NewGuid(), "1", 1, EdgeMutationKind.Delete, ReadOnlyMemory<byte>.Empty);
        await store.EnqueueAsync(deletion);
        Assert.True((await store.GetAsync(Scope, "1"))!.Deleted);
        Assert.Empty((await store.ReadPageAsync(Scope)).Items);
        var lease = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        await store.AcknowledgeAsync(lease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, new EdgeRecord("1", 2, ReadOnlyMemory<byte>.Empty, true)));
        Assert.True((await store.GetAsync(Scope, "1"))!.Deleted);
        Assert.Equal(2, (await store.GetAsync(Scope, "1"))!.ServerRevision);
    });

    [Fact]
    public Task Changes_and_acknowledgements_cannot_interleave_with_snapshot_cutover() => WithReadyStoreAsync(async (store, unusedOptions) =>
    {
        await store.EnqueueAsync(Mutation("1", 0, "pending"));
        var lease = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        var snapshot = new EdgeSnapshot(Guid.NewGuid(), 1);
        await store.BeginSnapshotAsync(Scope, snapshot);
        _ = await Assert.ThrowsAsync<EdgeScopeMismatchException>(() => store.ApplyChangesAsync(Scope, new EdgeChangeBatch(0, 2, [])).AsTask());
        _ = await Assert.ThrowsAsync<EdgeScopeMismatchException>(() => store.AcknowledgeAsync(lease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("1", 1, "applied"))).AsTask());
        await store.CommitSnapshotAsync(Scope, snapshot.Id);
        await store.AcknowledgeAsync(lease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("1", 1, "applied")));
    });

    [Fact]
    public Task Cache_queue_and_ack_receipt_transaction_rolls_back_when_receipt_storage_fails() => WithReadyStoreAsync(async (store, options) =>
    {
        await store.EnqueueAsync(Mutation("1", 0, "pending"));
        var lease = (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)))!;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_receipt BEFORE INSERT ON receipts BEGIN SELECT RAISE(ABORT,'injected receipt failure'); END;";
        _ = await command.ExecuteNonQueryAsync();
        _ = await Assert.ThrowsAsync<SqliteException>(() => store.AcknowledgeAsync(lease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("1", 1, "committed"))).AsTask());
        var current = (await store.GetAsync(Scope, "1"))!;
        Assert.Equal(0, current.ServerRevision);
        Assert.Equal(lease.Mutation.Id, current.PendingMutationId);
        command.CommandText = "DROP TRIGGER reject_receipt";
        _ = await command.ExecuteNonQueryAsync();
        await store.AcknowledgeAsync(lease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record("1", 1, "committed")));
        Assert.Null((await store.GetAsync(Scope, "1"))!.PendingMutationId);
    });

    [Fact]
    public Task Version_one_upgrade_preserves_pending_writes_and_unknown_versions_fail() => WithReadyStoreAsync(async (store, options) =>
    {
        await store.EnqueueAsync(Mutation("1", 0, "durable"));
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE receipts; UPDATE schema_metadata SET version=1";
        _ = await command.ExecuteNonQueryAsync();
        var upgraded = new SqliteEdgeStore(options);
        await upgraded.InitializeAsync();
        Assert.Equal("{\"value\":\"durable\"}", Json((await upgraded.GetAsync(Scope, "1"))!.Payload));
        command.CommandText = "UPDATE schema_metadata SET version=999";
        _ = await command.ExecuteNonQueryAsync();
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => upgraded.InitializeAsync().AsTask());
    });

    [Fact]
    public Task Bounded_sqlite_workload_persists_128_writes_and_pages_every_cached_document() => WithReadyStoreAsync(async (store, options) =>
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            var local = new SqliteEdgeStore(options);
            for (var i = 0; i < 32; i++)
            {
                await local.EnqueueAsync(Mutation($"{worker:D2}-{i:D3}", 0, new string('x', 1024)));
            }
        })));
        var seen = new HashSet<Guid>();
        while (await store.ClaimAsync(Scope, TimeSpan.FromMinutes(1)) is { } lease)
        {
            Assert.True(seen.Add(lease.Mutation.Id));
            await store.AcknowledgeAsync(lease, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record(lease.Mutation.DocumentId, 1, "committed")));
        }

        Assert.Equal(128, seen.Count);
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await store.ReadPageAsync(Scope, 17, cursor);
            ids.AddRange(page.Items.Select(static x => x.Id));
            cursor = page.NextAfterId;
        } while (cursor is not null);
        Assert.Equal(128, ids.Count);
        Assert.Equal(128, ids.Distinct(StringComparer.Ordinal).Count());
    });

    [Fact]
    public Task Synchronization_replays_one_stable_mutation_after_server_commit_before_local_ack_loss() => WithReadyStoreAsync(async (store, options) =>
    {
        await store.EnqueueAsync(Mutation("1", 0, "offline"));
        var remote = new CommitThenDisconnectTransport();
        var coordinator = new EdgeSynchronizationCoordinator(store, remote);
        _ = await Assert.ThrowsAsync<IOException>(() => coordinator.SynchronizeAsync(Scope).AsTask());
        Assert.Equal(1, remote.BusinessEffects);
        Assert.NotNull((await store.GetAsync(Scope, "1"))!.PendingMutationId);
        Assert.IsType<ManualClock>(options.TimeProvider).Advance(TimeSpan.FromMinutes(2));
        var restarted = new EdgeSynchronizationCoordinator(new SqliteEdgeStore(options), remote);
        await restarted.SynchronizeAsync(Scope);
        Assert.Equal(1, remote.BusinessEffects);
        Assert.Null((await store.GetAsync(Scope, "1"))!.PendingMutationId);
        Assert.Equal(1, (await store.GetAsync(Scope, "1"))!.ServerRevision);
    });

    [Fact]
    public Task Synchronization_session_allows_independent_writers_and_releases_durable_files() => WithReadyStoreAsync(async (store, options) =>
    {
        await using (await ((IEdgeSynchronizationSessionLocalStore)store).OpenSynchronizationSessionAsync())
        {
            Assert.True(File.Exists(options.DatabasePath + "-wal"));
            var independent = new SqliteEdgeStore(options);
            await independent.EnqueueAsync(Mutation("1", 0, "independent writer"));
            Assert.NotNull((await store.GetAsync(Scope, "1"))!.PendingMutationId);
            Assert.True(File.Exists(options.DatabasePath + "-wal"));
        }
        Assert.False(File.Exists(options.DatabasePath + "-wal"));
        using (File.Open(options.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        var reopened = new SqliteEdgeStore(options);
        Assert.NotNull((await reopened.GetAsync(Scope, "1"))!.PendingMutationId);
        Assert.Equal("{\"value\":\"independent writer\"}", Json((await reopened.GetAsync(Scope, "1"))!.Payload));
    });

    [Theory]
    [InlineData("success")]
    [InlineData("disconnect")]
    [InlineData("cancellation")]
    public Task Coordinator_releases_synchronization_session_on_every_exit(string exit) => WithReadyStoreAsync(async (store, options) =>
    {
        await store.EnqueueAsync(Mutation("1", 0, "offline"));
        using var cancellation = new CancellationTokenSource();
        var remote = new SessionObservingTransport(options.DatabasePath, exit, cancellation);
        var coordinator = new EdgeSynchronizationCoordinator(store, remote);
        if (exit == "disconnect")
        {
            _ = await Assert.ThrowsAsync<IOException>(() => coordinator.SynchronizeAsync(Scope, cancellationToken: cancellation.Token).AsTask());
        }
        else if (exit == "cancellation")
        {
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.SynchronizeAsync(Scope, cancellationToken: cancellation.Token).AsTask());
        }
        else { await coordinator.SynchronizeAsync(Scope, cancellationToken: cancellation.Token); }
        Assert.True(remote.SawOpenSession);
        Assert.False(File.Exists(options.DatabasePath + "-wal"));
        using (File.Open(options.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        var reopened = new SqliteEdgeStore(options);
        var document = await reopened.GetAsync(Scope, "1");
        Assert.NotNull(document);
        Assert.Equal(exit == "success", document.PendingMutationId is null);
        Assert.Equal(new EdgeCheckpoint(0, true), await reopened.GetCheckpointAsync(Scope));
    });

    [Fact]
    public Task Canceled_write_does_not_change_checkpoint_or_cache() => WithReadyStoreAsync(async (store, unusedOptions) =>
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ApplyChangesAsync(Scope, new EdgeChangeBatch(0, 1, [Record("1", 1, "no")]), cancellation.Token).AsTask());
        Assert.Equal(0, (await store.GetCheckpointAsync(Scope)).Position);
        Assert.Null(await store.GetAsync(Scope, "1"));
    });

    private static EdgeRecord Record(string id, long revision, string value) => new(id, revision, Payload(value));
    private static EdgeMutation Mutation(string id, long expectedRevision, string value) => new(Scope, Guid.NewGuid(), id, expectedRevision, EdgeMutationKind.Upsert, Payload(value));
    private static byte[] Payload(string value) => Encoding.UTF8.GetBytes("{\"value\":\"" + value + "\"}");
    private static string Json(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    private static async Task EmptySnapshotAsync(SqliteEdgeStore store, EdgeScope scope)
    {
        var snapshot = new EdgeSnapshot(Guid.NewGuid(), 0);
        await store.BeginSnapshotAsync(scope, snapshot);
        await store.CommitSnapshotAsync(scope, snapshot.Id);
    }

    private static async Task WithReadyStoreAsync(Func<SqliteEdgeStore, SqliteEdgeOptions, Task> test, SqliteEdgeOptions? options = null) =>
        await WithStoreAsync(async (store, configured) =>
        {
            await EmptySnapshotAsync(store, Scope);
            await test(store, configured);
        }, options);

    private static async Task WithStoreAsync(Func<SqliteEdgeStore, SqliteEdgeOptions, Task> test, SqliteEdgeOptions? options = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bluetusk-edge-" + Guid.NewGuid().ToString("N"));
        var configured = (options ?? new SqliteEdgeOptions { DatabasePath = "placeholder" }) with { DatabasePath = Path.Combine(directory, "cache.db"), TimeProvider = new ManualClock() };
        var store = new SqliteEdgeStore(configured);
        try
        {
            await store.InitializeAsync();
            await store.ActivateScopeAsync(Scope);
            await test(store, configured);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                var resolved = Path.GetFullPath(directory);
                var allowed = Path.GetFullPath(Path.GetTempPath()) + (Path.EndsInDirectorySeparator(Path.GetTempPath()) ? string.Empty : Path.DirectorySeparatorChar);
                if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("bluetusk-edge-", StringComparison.Ordinal))
                {
                    Directory.Delete(resolved, recursive: true);
                }
            }
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class CommitThenDisconnectTransport : IEdgeRemoteTransport
    {
        private readonly Dictionary<Guid, EdgeMutationOutcome> _receipts = [];
        public int BusinessEffects { get; private set; }
        public ValueTask<EdgeSnapshot> BeginSnapshotAsync(EdgeScope scope, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This fixture already has a snapshot.");
        public IAsyncEnumerable<IReadOnlyList<EdgeRecord>> ReadSnapshotAsync(EdgeScope scope, EdgeSnapshot snapshot, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This fixture already has a snapshot.");
        public ValueTask<EdgeChangeBatch?> ReadChangesAsync(EdgeScope scope, long afterPosition, int maxRecords, CancellationToken cancellationToken = default) => ValueTask.FromResult<EdgeChangeBatch?>(null);
        public ValueTask<EdgeMutationOutcome> ApplyMutationAsync(EdgeMutation mutation, CancellationToken cancellationToken = default)
        {
            if (_receipts.TryGetValue(mutation.Id, out var replay))
            {
                return ValueTask.FromResult(replay);
            }

            BusinessEffects++;
            _receipts.Add(mutation.Id, new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record(mutation.DocumentId, 1, "server committed")));
            throw new IOException("Injected disconnect after simulated server commit.");
        }
    }

    private sealed class OrderedReceiptTransport : IEdgeRemoteTransport, IEdgeOrderedReceiptTransport
    {
        public int Applies { get; private set; }
        public int Confirmations { get; private set; }
        public int HorizonAttempts { get; private set; }
        public int? FailConfirmationAt { get; set; }
        public bool LoseFirstHorizon { get; set; } = true;
        public ValueTask<EdgeSnapshot> BeginSnapshotAsync(EdgeScope scope, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The local snapshot is ready.");
        public IAsyncEnumerable<IReadOnlyList<EdgeRecord>> ReadSnapshotAsync(EdgeScope scope, EdgeSnapshot snapshot, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The local snapshot is ready.");
        public ValueTask<EdgeChangeBatch?> ReadChangesAsync(EdgeScope scope, long afterPosition, int maxRecords, CancellationToken cancellationToken = default) => ValueTask.FromResult<EdgeChangeBatch?>(null);
        public ValueTask<EdgeMutationOutcome> ApplyMutationAsync(EdgeMutation mutation, CancellationToken cancellationToken = default)
        { Applies++; return ValueTask.FromResult(new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record(mutation.DocumentId, 1, "server"))); }
        public ValueTask FinalizeMutationReceiptAsync(EdgeMutation mutation, CancellationToken cancellationToken = default)
        { if (++Confirmations == FailConfirmationAt) { throw new IOException("Injected confirmation failure."); } return ValueTask.CompletedTask; }
        public ValueTask AdvanceOrderedReceiptHorizonAsync(EdgeScope scope, Guid throughMutationId, int maxReceipts = 1000, CancellationToken cancellationToken = default)
        { if (++HorizonAttempts == 1 && LoseFirstHorizon) { throw new IOException("Injected lost horizon response."); } return ValueTask.CompletedTask; }
    }

    private sealed class SessionObservingTransport(string databasePath, string exit, CancellationTokenSource cancellation) : IEdgeRemoteTransport
    {
        public bool SawOpenSession { get; private set; }
        public ValueTask<EdgeSnapshot> BeginSnapshotAsync(EdgeScope scope, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The local snapshot is ready.");
        public IAsyncEnumerable<IReadOnlyList<EdgeRecord>> ReadSnapshotAsync(EdgeScope scope, EdgeSnapshot snapshot, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The local snapshot is ready.");
        public ValueTask<EdgeChangeBatch?> ReadChangesAsync(EdgeScope scope, long afterPosition, int maxRecords, CancellationToken cancellationToken = default) => ValueTask.FromResult<EdgeChangeBatch?>(null);
        public ValueTask<EdgeMutationOutcome> ApplyMutationAsync(EdgeMutation mutation, CancellationToken cancellationToken = default)
        {
            SawOpenSession = File.Exists(databasePath + "-wal");
            Assert.True(SawOpenSession);
            if (exit == "disconnect") { throw new IOException("Injected remote disconnect during an open session."); }
            if (exit == "cancellation") { cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            return ValueTask.FromResult(new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record(mutation.DocumentId, 1, "server")));
        }
    }

    private sealed class SuccessfulTransport : IEdgeRemoteTransport
    {
        public int Applies { get; private set; }
        public ValueTask<EdgeSnapshot> BeginSnapshotAsync(EdgeScope scope, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The local snapshot is ready.");
        public IAsyncEnumerable<IReadOnlyList<EdgeRecord>> ReadSnapshotAsync(EdgeScope scope, EdgeSnapshot snapshot, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The local snapshot is ready.");
        public ValueTask<EdgeChangeBatch?> ReadChangesAsync(EdgeScope scope, long afterPosition, int maxRecords, CancellationToken cancellationToken = default) => ValueTask.FromResult<EdgeChangeBatch?>(null);
        public ValueTask<EdgeMutationOutcome> ApplyMutationAsync(EdgeMutation mutation, CancellationToken cancellationToken = default)
        {
            Applies++;
            return ValueTask.FromResult(new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record(mutation.DocumentId, 1, "server")));
        }
    }

    private sealed class FailSecondTransport : IEdgeRemoteTransport
    {
        public int Attempts { get; private set; }
        public ValueTask<EdgeSnapshot> BeginSnapshotAsync(EdgeScope scope, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The local snapshot is ready.");
        public IAsyncEnumerable<IReadOnlyList<EdgeRecord>> ReadSnapshotAsync(EdgeScope scope, EdgeSnapshot snapshot, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The local snapshot is ready.");
        public ValueTask<EdgeChangeBatch?> ReadChangesAsync(EdgeScope scope, long afterPosition, int maxRecords, CancellationToken cancellationToken = default) => ValueTask.FromResult<EdgeChangeBatch?>(null);
        public ValueTask<EdgeMutationOutcome> ApplyMutationAsync(EdgeMutation mutation, CancellationToken cancellationToken = default) =>
            ++Attempts == 2 ? ValueTask.FromException<EdgeMutationOutcome>(new IOException("Injected second ordered request failure.")) :
                ValueTask.FromResult(new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, Record(mutation.DocumentId, 1, "server")));
    }
}

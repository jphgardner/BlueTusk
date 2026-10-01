using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlueTusk.Edge;

public sealed record EdgeScope
{
    public EdgeScope(string tenant, string id, long epoch)
    {
        EdgeValidation.Key(tenant, nameof(tenant), 256);
        EdgeValidation.Key(id, nameof(id), 256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(epoch);
        Tenant = tenant;
        Id = id;
        Epoch = epoch;
    }

    public string Tenant { get; }
    public string Id { get; }
    public long Epoch { get; }
}

public enum EdgeMutationKind { Upsert, Delete }
public enum EdgeMutationStatus { Pending, Leased, Conflict }
public enum EdgeMutationOutcomeKind { Applied, Conflict }
public enum EdgeEpochChangePolicy { RejectIfPending, DiscardPending }

public sealed class EdgeRecord
{
    public EdgeRecord(string id, long revision, ReadOnlyMemory<byte> payload, bool deleted = false)
    {
        EdgeValidation.Key(id, nameof(id), 512);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        if (deleted && !payload.IsEmpty)
        {
            throw new ArgumentException("A tombstone cannot carry document content.", nameof(payload));
        }

        if (!deleted)
        {
            EdgeValidation.JsonObject(payload);
        }

        Id = id;
        Revision = revision;
        Payload = payload.ToArray();
        Deleted = deleted;
    }

    public string Id { get; }
    public long Revision { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public bool Deleted { get; }
}

public sealed class EdgeMutation
{
    public EdgeMutation(EdgeScope scope, Guid id, string documentId, long expectedRevision, EdgeMutationKind kind, ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EdgeValidation.Key(documentId, nameof(documentId), 512);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if (id == Guid.Empty || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("Mutations require a nonempty stable identity and a supported kind.");
        }

        if (kind is EdgeMutationKind.Delete && !payload.IsEmpty)
        {
            throw new ArgumentException("A queued deletion cannot carry a payload.", nameof(payload));
        }

        if (kind is EdgeMutationKind.Upsert)
        {
            EdgeValidation.JsonObject(payload);
        }

        Scope = scope;
        Id = id;
        DocumentId = documentId;
        ExpectedRevision = expectedRevision;
        Kind = kind;
        Payload = payload.ToArray();
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{scope.Tenant}\0{scope.Id}\0{scope.Epoch}\0{documentId}\0{expectedRevision}\0{kind}\0{Convert.ToBase64String(payload.Span)}"))));
    }

    public EdgeScope Scope { get; }
    public Guid Id { get; }
    public string DocumentId { get; }
    public long ExpectedRevision { get; }
    public EdgeMutationKind Kind { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public string Fingerprint { get; }
}

public sealed record EdgeCachedDocument(string Id, long ServerRevision, ReadOnlyMemory<byte> Payload, bool Deleted, Guid? PendingMutationId, EdgeMutationStatus? PendingStatus);
public sealed record EdgeCachePage(IReadOnlyList<EdgeCachedDocument> Items, string? NextAfterId);
public sealed record EdgeCheckpoint(long Position, bool SnapshotReady);
public sealed record EdgeMutationLease(EdgeMutation Mutation, long Fence, DateTimeOffset ExpiresAt);
public sealed record EdgeMutationOutcome(EdgeMutationOutcomeKind Kind, EdgeRecord? ServerRecord);
public sealed class EdgeMutationAcknowledgement
{
    public EdgeMutationAcknowledgement(EdgeMutationLease lease, EdgeMutationOutcome outcome)
    {
        Lease = lease ?? throw new ArgumentNullException(nameof(lease));
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
    }

    public EdgeMutationLease Lease { get; }
    public EdgeMutationOutcome Outcome { get; }
}
public sealed record EdgeChangeBatch(long FromPosition, long ToPosition, IReadOnlyList<EdgeRecord> Records);
public sealed record EdgeSnapshot(Guid Id, long Position);

public interface IEdgeLocalStore
{
    ValueTask ActivateScopeAsync(EdgeScope scope, EdgeEpochChangePolicy policy = EdgeEpochChangePolicy.RejectIfPending, CancellationToken cancellationToken = default);
    ValueTask<EdgeCheckpoint> GetCheckpointAsync(EdgeScope scope, CancellationToken cancellationToken = default);
    ValueTask<EdgeCachedDocument?> GetAsync(EdgeScope scope, string id, CancellationToken cancellationToken = default);
    ValueTask<EdgeCachePage> ReadPageAsync(EdgeScope scope, int pageSize = 100, string? afterId = null, CancellationToken cancellationToken = default);
    ValueTask EnqueueAsync(EdgeMutation mutation, CancellationToken cancellationToken = default);
    ValueTask<EdgeMutationLease?> ClaimAsync(EdgeScope scope, TimeSpan leaseDuration, CancellationToken cancellationToken = default);
    ValueTask AcknowledgeAsync(EdgeMutationLease lease, EdgeMutationOutcome outcome, CancellationToken cancellationToken = default);
    ValueTask BeginSnapshotAsync(EdgeScope scope, EdgeSnapshot snapshot, CancellationToken cancellationToken = default);
    ValueTask ApplySnapshotBatchAsync(EdgeScope scope, Guid snapshotId, IReadOnlyList<EdgeRecord> records, CancellationToken cancellationToken = default);
    ValueTask CommitSnapshotAsync(EdgeScope scope, Guid snapshotId, CancellationToken cancellationToken = default);
    ValueTask ApplyChangesAsync(EdgeScope scope, EdgeChangeBatch batch, CancellationToken cancellationToken = default);
}

/// <summary>Optional storage resource lifetime for one synchronization pass. A session must not hold a transaction or reader across remote requests.</summary>
public interface IEdgeSynchronizationSessionLocalStore : IEdgeLocalStore
{
    ValueTask<IAsyncDisposable> OpenSynchronizationSessionAsync(CancellationToken cancellationToken = default);
}

/// <summary>Optional atomic acknowledgement and next-claim transition for durable local stores.</summary>
public interface IEdgeChainedLocalStore : IEdgeLocalStore
{
    ValueTask<EdgeMutationLease?> AcknowledgeAndClaimNextAsync(EdgeMutationLease lease, EdgeMutationOutcome outcome,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default);
}

/// <summary>Optional ordered-prefix claim and atomic acknowledgement for bounded catch-up after reconnect.</summary>
public interface IEdgeOrderedBatchLocalStore : IEdgeChainedLocalStore, IEdgeOrderedLocalStore
{
    /// <summary>Returns null when the earliest work needs the ordinary claim path; an empty list means no claimable work.</summary>
    ValueTask<IReadOnlyList<EdgeMutationLease>?> ClaimOrderedBatchAsync(EdgeScope scope, int maximum,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default);
    ValueTask AcknowledgeBatchAsync(IReadOnlyList<EdgeMutationAcknowledgement> acknowledgements,
        CancellationToken cancellationToken = default);
    /// <summary>Returns unprocessed leases to the queue while fencing their former owners.</summary>
    ValueTask ReleaseOrderedBatchAsync(IReadOnlyList<EdgeMutationLease> leases,
        CancellationToken cancellationToken = default);
}

/// <summary>Host-owned authenticated transport. Server mutation identity receipts must commit atomically with business effects.</summary>
public interface IEdgeRemoteTransport
{
    ValueTask<EdgeSnapshot> BeginSnapshotAsync(EdgeScope scope, CancellationToken cancellationToken = default);
    IAsyncEnumerable<IReadOnlyList<EdgeRecord>> ReadSnapshotAsync(EdgeScope scope, EdgeSnapshot snapshot, CancellationToken cancellationToken = default);
    ValueTask<EdgeChangeBatch?> ReadChangesAsync(EdgeScope scope, long afterPosition, int maxRecords, CancellationToken cancellationToken = default);
    ValueTask<EdgeMutationOutcome> ApplyMutationAsync(EdgeMutation mutation, CancellationToken cancellationToken = default);
}

/// <summary>Optional durable ordered-receipt work queue implemented by local stores that allocate identities atomically with enqueue.</summary>
public interface IEdgeOrderedLocalStore : IEdgeLocalStore
{
    ValueTask<EdgeMutation> EnqueueOrderedAsync(EdgeScope scope, string documentId, long expectedRevision, EdgeMutationKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    ValueTask<EdgeMutation?> ReadNextUnconfirmedOrderedReceiptAsync(EdgeScope scope, CancellationToken cancellationToken = default);
    ValueTask MarkOrderedReceiptConfirmedAsync(EdgeMutation mutation, CancellationToken cancellationToken = default);
    ValueTask<Guid?> ReadConfirmedOrderedHorizonAsync(EdgeScope scope, int maxReceipts = 1000, CancellationToken cancellationToken = default);
    ValueTask MarkOrderedReceiptHorizonAsync(EdgeScope scope, Guid throughMutationId, CancellationToken cancellationToken = default);
}

/// <summary>Optional bounded bulk access to the durable ordered-receipt outbox.</summary>
public interface IEdgeOrderedReceiptBatchLocalStore : IEdgeOrderedLocalStore
{
    ValueTask<IReadOnlyList<EdgeMutation>> ReadUnconfirmedOrderedReceiptsAsync(EdgeScope scope, int maxReceipts, CancellationToken cancellationToken = default);
    ValueTask MarkOrderedReceiptsConfirmedAsync(EdgeScope scope, IReadOnlyList<EdgeMutation> mutations, CancellationToken cancellationToken = default);
}

/// <summary>Optional remote confirmation and horizon contract. Confirm only after a durable local outcome acknowledgement.</summary>
public interface IEdgeOrderedReceiptTransport
{
    ValueTask FinalizeMutationReceiptAsync(EdgeMutation mutation, CancellationToken cancellationToken = default);
    ValueTask AdvanceOrderedReceiptHorizonAsync(EdgeScope scope, Guid throughMutationId, int maxReceipts = 1000, CancellationToken cancellationToken = default);
}

public sealed class EdgeSynchronizationCoordinator(IEdgeLocalStore store, IEdgeRemoteTransport transport)
{
    public async ValueTask SynchronizeAsync(EdgeScope scope, int maxPushes = 32, int maxChangeBatches = 32, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPushes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPushes, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChangeBatches, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxChangeBatches, 1000);
        await using var session = store is IEdgeSynchronizationSessionLocalStore sessionStore
            ? await sessionStore.OpenSynchronizationSessionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var checkpoint = await store.GetCheckpointAsync(scope, cancellationToken).ConfigureAwait(false);
        if (!checkpoint.SnapshotReady)
        {
            var snapshot = await transport.BeginSnapshotAsync(scope, cancellationToken).ConfigureAwait(false);
            await store.BeginSnapshotAsync(scope, snapshot, cancellationToken).ConfigureAwait(false);
            var batches = 0;
            await foreach (var batch in transport.ReadSnapshotAsync(scope, snapshot, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (++batches > 100_000)
                {
                    throw new EdgeCapacityException("Snapshot exceeded the maximum bounded batches.");
                }

                await store.ApplySnapshotBatchAsync(scope, snapshot.Id, batch, cancellationToken).ConfigureAwait(false);
            }

            await store.CommitSnapshotAsync(scope, snapshot.Id, cancellationToken).ConfigureAwait(false);
        }

        if (store is IEdgeOrderedLocalStore orderedLocal && transport is IEdgeOrderedReceiptTransport orderedRemote)
        { await FlushOrderedReceiptsAsync(orderedLocal, orderedRemote, scope, maxPushes, cancellationToken).ConfigureAwait(false); }

        var batchStore = store as IEdgeOrderedBatchLocalStore;
        var claimedBatch = batchStore is null ? null :
            await batchStore.ClaimOrderedBatchAsync(scope, maxPushes, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        if (claimedBatch is not null)
        {
            var acknowledged = new List<EdgeMutationAcknowledgement>(claimedBatch.Count);
            try
            {
                foreach (var lease in claimedBatch)
                {
                    var outcome = await transport.ApplyMutationAsync(lease.Mutation, cancellationToken).ConfigureAwait(false);
                    acknowledged.Add(new EdgeMutationAcknowledgement(lease, outcome));
                }
            }
            finally
            {
                // Successful remote outcomes must be made durable even if a later request fails.
                var committed = false;
                try
                {
                    if (acknowledged.Count != 0)
                    { await batchStore!.AcknowledgeBatchAsync(acknowledged, CancellationToken.None).ConfigureAwait(false); }
                    committed = true;
                }
                finally
                {
                    IReadOnlyList<EdgeMutationLease> unreleased = committed ? claimedBatch.Skip(acknowledged.Count).ToArray() : claimedBatch;
                    if (unreleased.Count != 0)
                    { await batchStore!.ReleaseOrderedBatchAsync(unreleased, CancellationToken.None).ConfigureAwait(false); }
                }
            }
        }
        else
        {
            EdgeMutationLease? nextLease = null;
            for (var i = 0; i < maxPushes; i++)
            {
                var lease = nextLease ?? await store.ClaimAsync(scope, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                if (lease is null) { break; }
                var outcome = await transport.ApplyMutationAsync(lease.Mutation, cancellationToken).ConfigureAwait(false);
                if (store is IEdgeChainedLocalStore chained && i + 1 < maxPushes)
                {
                    nextLease = await chained.AcknowledgeAndClaimNextAsync(lease, outcome, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                    if (nextLease is null) { break; }
                }
                else
                {
                    await store.AcknowledgeAsync(lease, outcome, cancellationToken).ConfigureAwait(false);
                    nextLease = null;
                }
            }
        }

        if (store is IEdgeOrderedLocalStore orderedAfterPush && transport is IEdgeOrderedReceiptTransport remoteAfterPush)
        { await FlushOrderedReceiptsAsync(orderedAfterPush, remoteAfterPush, scope, maxPushes, cancellationToken).ConfigureAwait(false); }

        for (var i = 0; i < maxChangeBatches; i++)
        {
            checkpoint = await store.GetCheckpointAsync(scope, cancellationToken).ConfigureAwait(false);
            var batch = await transport.ReadChangesAsync(scope, checkpoint.Position, 512, cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                break;
            }

            await store.ApplyChangesAsync(scope, batch, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask FlushOrderedReceiptsAsync(IEdgeOrderedLocalStore store, IEdgeOrderedReceiptTransport transport,
        EdgeScope scope, int maximum, CancellationToken cancellationToken)
    {
        if (store is IEdgeOrderedReceiptBatchLocalStore batchStore)
        {
            var pending = await batchStore.ReadUnconfirmedOrderedReceiptsAsync(scope, maximum, cancellationToken).ConfigureAwait(false);
            var confirmed = new List<EdgeMutation>(pending.Count);
            try
            {
                foreach (var mutation in pending)
                {
                    await transport.FinalizeMutationReceiptAsync(mutation, cancellationToken).ConfigureAwait(false);
                    confirmed.Add(mutation);
                }
            }
            finally
            {
                // A successfully finalized receipt remains retryable until the horizon advances.
                // Persist the successful prefix even when a later request fails or is cancelled.
                if (confirmed.Count != 0)
                {
                    await batchStore.MarkOrderedReceiptsConfirmedAsync(scope, confirmed, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        else
        {
            for (var i = 0; i < maximum; i++)
            {
                var mutation = await store.ReadNextUnconfirmedOrderedReceiptAsync(scope, cancellationToken).ConfigureAwait(false);
                if (mutation is null) { break; }
                await transport.FinalizeMutationReceiptAsync(mutation, cancellationToken).ConfigureAwait(false);
                await store.MarkOrderedReceiptConfirmedAsync(mutation, cancellationToken).ConfigureAwait(false);
            }
        }
        var through = await store.ReadConfirmedOrderedHorizonAsync(scope, maximum, cancellationToken).ConfigureAwait(false);
        if (through is Guid id)
        {
            await transport.AdvanceOrderedReceiptHorizonAsync(scope, id, maximum, cancellationToken).ConfigureAwait(false);
            await store.MarkOrderedReceiptHorizonAsync(scope, id, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class EdgeCapacityException(string message) : Exception(message);
public sealed class EdgeScopeMismatchException() : Exception("The scope epoch is inactive, changed, or missing.");
public sealed class EdgeCheckpointMismatchException() : Exception("The changes do not continue from the committed local checkpoint.");
public sealed class EdgeMutationIdentityException() : Exception("The stable mutation identity was reused for a different operation or outcome.");
public sealed class EdgeLeaseLostException() : Exception("The mutation lease is expired or its fence was replaced.");
public sealed class EdgeRevisionConflictException() : Exception("The document revision or payload is incompatible with its cached or queued state.");

public static class EdgeValidation
{
    public static void Key(string value, string parameterName, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(value) > maxBytes)
        {
            throw new ArgumentException($"Value must fit within {maxBytes} UTF-8 bytes and cannot contain NUL.", parameterName);
        }
    }

    public static void JsonObject(ReadOnlyMemory<byte> payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("An offline document must be a JSON object.", nameof(payload));
        }
    }
}

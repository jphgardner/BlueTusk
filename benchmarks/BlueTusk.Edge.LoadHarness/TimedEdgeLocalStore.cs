using System.Diagnostics;
using BlueTusk.Edge.Sqlite;

namespace BlueTusk.Edge.LoadHarness;

internal sealed class EdgeLocalPhaseTimings
{
    internal LatencyCapture Checkpoint { get; } = new();
    internal LatencyCapture Claim { get; } = new();
    internal LatencyCapture Acknowledge { get; } = new();
    internal LatencyCapture ReadReceipt { get; } = new();
    internal LatencyCapture ConfirmReceipt { get; } = new();
    internal LatencyCapture ReadHorizon { get; } = new();
    internal LatencyCapture AdvanceHorizon { get; } = new();
    internal LatencyCapture ApplyChanges { get; } = new();
}

internal sealed class TimedEdgeLocalStore(SqliteEdgeStore inner, EdgeLocalPhaseTimings timings) : IEdgeOrderedLocalStore
{
    public ValueTask ActivateScopeAsync(EdgeScope scope, EdgeEpochChangePolicy policy = EdgeEpochChangePolicy.RejectIfPending,
        CancellationToken cancellationToken = default) => inner.ActivateScopeAsync(scope, policy, cancellationToken);

    public async ValueTask<EdgeCheckpoint> GetCheckpointAsync(EdgeScope scope, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await inner.GetCheckpointAsync(scope, cancellationToken).ConfigureAwait(false);
        timings.Checkpoint.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    public ValueTask<EdgeCachedDocument?> GetAsync(EdgeScope scope, string id, CancellationToken cancellationToken = default) =>
        inner.GetAsync(scope, id, cancellationToken);

    public ValueTask<EdgeCachePage> ReadPageAsync(EdgeScope scope, int pageSize = 100, string? afterId = null,
        CancellationToken cancellationToken = default) => inner.ReadPageAsync(scope, pageSize, afterId, cancellationToken);

    public ValueTask EnqueueAsync(EdgeMutation mutation, CancellationToken cancellationToken = default) =>
        inner.EnqueueAsync(mutation, cancellationToken);

    public ValueTask<EdgeMutation> EnqueueOrderedAsync(EdgeScope scope, string documentId, long expectedRevision,
        EdgeMutationKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        inner.EnqueueOrderedAsync(scope, documentId, expectedRevision, kind, payload, cancellationToken);

    public async ValueTask<EdgeMutationLease?> ClaimAsync(EdgeScope scope, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await inner.ClaimAsync(scope, leaseDuration, cancellationToken).ConfigureAwait(false);
        timings.Claim.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    public async ValueTask AcknowledgeAsync(EdgeMutationLease lease, EdgeMutationOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        await inner.AcknowledgeAsync(lease, outcome, cancellationToken).ConfigureAwait(false);
        timings.Acknowledge.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public ValueTask BeginSnapshotAsync(EdgeScope scope, EdgeSnapshot snapshot, CancellationToken cancellationToken = default) =>
        inner.BeginSnapshotAsync(scope, snapshot, cancellationToken);

    public ValueTask ApplySnapshotBatchAsync(EdgeScope scope, Guid snapshotId, IReadOnlyList<EdgeRecord> records,
        CancellationToken cancellationToken = default) => inner.ApplySnapshotBatchAsync(scope, snapshotId, records, cancellationToken);

    public ValueTask CommitSnapshotAsync(EdgeScope scope, Guid snapshotId, CancellationToken cancellationToken = default) =>
        inner.CommitSnapshotAsync(scope, snapshotId, cancellationToken);

    public async ValueTask ApplyChangesAsync(EdgeScope scope, EdgeChangeBatch batch, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        await inner.ApplyChangesAsync(scope, batch, cancellationToken).ConfigureAwait(false);
        timings.ApplyChanges.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public async ValueTask<EdgeMutation?> ReadNextUnconfirmedOrderedReceiptAsync(EdgeScope scope,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await inner.ReadNextUnconfirmedOrderedReceiptAsync(scope, cancellationToken).ConfigureAwait(false);
        timings.ReadReceipt.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    public async ValueTask MarkOrderedReceiptConfirmedAsync(EdgeMutation mutation, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        await inner.MarkOrderedReceiptConfirmedAsync(mutation, cancellationToken).ConfigureAwait(false);
        timings.ConfirmReceipt.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public async ValueTask<Guid?> ReadConfirmedOrderedHorizonAsync(EdgeScope scope, int maxReceipts = 1000,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await inner.ReadConfirmedOrderedHorizonAsync(scope, maxReceipts, cancellationToken).ConfigureAwait(false);
        timings.ReadHorizon.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    public async ValueTask MarkOrderedReceiptHorizonAsync(EdgeScope scope, Guid throughMutationId,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        await inner.MarkOrderedReceiptHorizonAsync(scope, throughMutationId, cancellationToken).ConfigureAwait(false);
        timings.AdvanceHorizon.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}

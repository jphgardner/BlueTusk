using System.Collections.ObjectModel;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync.Nats;

public enum NatsSyncEnvelopeKind : byte
{
    Transaction = 1,
    SnapshotReset = 2,
    SnapshotStart = 3,
    SnapshotBatch = 4,
    SnapshotComplete = 5,
}

public sealed class NatsSyncEnvelope
{
    private readonly ReadOnlyCollection<NatsSyncMutation> _mutations;

    internal NatsSyncEnvelope(
        int formatVersion,
        NatsSyncEnvelopeKind kind,
        string pipelineId,
        SyncTransformVersion transform,
        ChangeSourceIdentity source,
        NatsSyncTransaction? transaction,
        NatsSyncSnapshot? snapshot,
        IEnumerable<NatsSyncMutation> mutations)
    {
        FormatVersion = formatVersion;
        Kind = kind;
        PipelineId = pipelineId;
        Transform = transform;
        Source = source;
        Transaction = transaction;
        Snapshot = snapshot;
        _mutations = Array.AsReadOnly(mutations.ToArray());
    }

    public int FormatVersion { get; }

    public NatsSyncEnvelopeKind Kind { get; }

    public string PipelineId { get; }

    public SyncTransformVersion Transform { get; }

    public ChangeSourceIdentity Source { get; }

    public NatsSyncTransaction? Transaction { get; }

    public NatsSyncSnapshot? Snapshot { get; }

    public IReadOnlyList<NatsSyncMutation> Mutations => _mutations;
}

public sealed record NatsSyncTransaction(
    uint TransactionId,
    BlueTuskLogSequenceNumber CommitEndPosition,
    DateTimeOffset CommitTimestamp,
    ChangeTransactionOutcome Outcome,
    string? GlobalTransactionId);

public sealed record NatsSyncSnapshot(
    Guid Epoch,
    BlueTuskLogSequenceNumber ConsistentPosition,
    DateTimeOffset StartedAt,
    Guid? AbandonedEpoch,
    string? Reason,
    int? TableCount,
    long? RowCount,
    string? TableIdentity,
    long? BatchSequence,
    bool? IsLastForTable);

public sealed record NatsSyncMutation(
    string StableId,
    SyncMutationKind Kind,
    string Collection,
    string? Key,
    ReadOnlyMemory<byte> Content,
    string? ContentType,
    string? PartitionKey);

public static class NatsSyncEnvelopeReader
{
    public static NatsSyncEnvelope Decode(ReadOnlySpan<byte> payload) =>
        NatsSyncEnvelopeCodec.Decode(payload);
}

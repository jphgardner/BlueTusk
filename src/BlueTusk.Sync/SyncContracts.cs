using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync;

[Flags]
public enum SyncDestinationCapabilities
{
    None = 0,
    TransactionalBatches = 1 << 0,
    IdempotentUpserts = 1 << 1,
    Deletes = 1 << 2,
    CoLocatedCheckpoint = 1 << 3,
    Reconciliation = 1 << 4,
    AliasSwap = 1 << 5,
}

public enum SyncPipelineState
{
    Provisioning,
    Snapshotting,
    CatchingUp,
    Running,
    Paused,
    Rebuilding,
    Reconciling,
    Faulted,
    Stopped,
}

public enum SyncPoisonRecordPolicy
{
    Pause,
    QuarantineAndAdvance,
    QuarantineAndPause,
}

public enum SyncMutationKind
{
    Upsert,
    Delete,
    DeleteCollection,
}

public enum SyncProvisionStatus
{
    Ready,
    RebuildRequired,
}

public enum SyncApplyStatus
{
    Applied,
    AlreadyApplied,
    TransformVersionMismatch,
    Rejected,
}

public sealed record SyncTransformVersion
{
    public const int CurrentFingerprintFormatVersion = 1;

    public const int MinimumSupportedFingerprintFormatVersion = 1;

    public SyncTransformVersion(string name, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "A transform fingerprint must be a 64-character SHA-256 hexadecimal value.",
                nameof(fingerprint));
        }

        Name = name;
        Fingerprint = fingerprint.ToLowerInvariant();
    }

    public string Name { get; }

    public string Fingerprint { get; }

    public static SyncTransformVersion Create(
        string name,
        string version,
        ReadOnlySpan<byte> canonicalConfiguration = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, name);
        Append(hash, version);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, canonicalConfiguration.Length);
        hash.AppendData(length);
        hash.AppendData(canonicalConfiguration);
        return new SyncTransformVersion(name, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public sealed record SyncMutation
{
    public SyncMutation(
        ChangeId changeId,
        SyncMutationKind kind,
        string collection,
        string? key,
        ReadOnlyMemory<byte> content,
        string? contentType = null,
        string? partitionKey = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        if (kind is not SyncMutationKind.DeleteCollection)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
        }
        if (kind is not SyncMutationKind.Upsert && !content.IsEmpty)
        {
            throw new ArgumentException("Delete mutations cannot contain replacement content.", nameof(content));
        }

        if (kind is SyncMutationKind.Upsert)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        }

        ChangeId = changeId;
        Kind = kind;
        Collection = collection;
        Key = key;
        Content = content;
        ContentType = contentType;
        PartitionKey = partitionKey;
    }

    public ChangeId ChangeId { get; }

    public SyncMutationKind Kind { get; }

    public string Collection { get; }

    public string? Key { get; }

    public ReadOnlyMemory<byte> Content { get; }

    public string? ContentType { get; }

    public string? PartitionKey { get; }
}

public sealed record SyncSnapshotMutation
{
    public SyncSnapshotMutation(
        SnapshotRowId rowId,
        string collection,
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        string? partitionKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        RowId = rowId;
        Collection = collection;
        Key = key;
        Content = content;
        ContentType = contentType;
        PartitionKey = partitionKey;
    }

    public SnapshotRowId RowId { get; }

    public string Collection { get; }

    public string Key { get; }

    public ReadOnlyMemory<byte> Content { get; }

    public string ContentType { get; }

    public string? PartitionKey { get; }
}

public sealed class SyncTransactionBatch
{
    private readonly ReadOnlyCollection<SyncMutation> _mutations;

    public SyncTransactionBatch(
        string pipelineId,
        SyncTransformVersion transform,
        ChangeTransaction transaction,
        IEnumerable<SyncMutation> mutations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(mutations);
        PipelineId = pipelineId;
        Transform = transform;
        Transaction = new SyncSourceTransaction(
            transaction.Source,
            transaction.TransactionId,
            transaction.CommitEndPosition,
            transaction.CommitTimestamp,
            transaction.Outcome,
            transaction.GlobalTransactionId);
        _mutations = Array.AsReadOnly(mutations.ToArray());
        if (_mutations.Any(mutation =>
                mutation.ChangeId.Source != transaction.Source ||
                mutation.ChangeId.CommitEndPosition != transaction.CommitEndPosition ||
                mutation.ChangeId.TransactionId != transaction.TransactionId))
        {
            throw new ArgumentException(
                "Every mutation must retain the source transaction identity.",
                nameof(mutations));
        }
    }

    public string PipelineId { get; }

    public SyncTransformVersion Transform { get; }

    public SyncSourceTransaction Transaction { get; }

    public IReadOnlyList<SyncMutation> Mutations => _mutations;
}

public sealed record SyncSourceTransaction(
    ChangeSourceIdentity Source,
    uint TransactionId,
    BlueTuskLogSequenceNumber CommitEndPosition,
    DateTimeOffset CommitTimestamp,
    ChangeTransactionOutcome Outcome,
    string? GlobalTransactionId);

public sealed class SyncSnapshotBatch
{
    private readonly ReadOnlyCollection<SyncSnapshotMutation> _mutations;

    public SyncSnapshotBatch(
        string pipelineId,
        SyncTransformVersion transform,
        ChangeSnapshotBatch sourceBatch,
        IEnumerable<SyncSnapshotMutation> mutations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(sourceBatch);
        ArgumentNullException.ThrowIfNull(mutations);
        PipelineId = pipelineId;
        Transform = transform;
        SourceBatch = sourceBatch;
        _mutations = Array.AsReadOnly(mutations.ToArray());
        if (_mutations.Any(mutation => mutation.RowId.Epoch != sourceBatch.Epoch.Value))
        {
            throw new ArgumentException(
                "Every snapshot mutation must retain the source epoch.",
                nameof(mutations));
        }
    }

    public string PipelineId { get; }

    public SyncTransformVersion Transform { get; }

    public ChangeSnapshotBatch SourceBatch { get; }

    public IReadOnlyList<SyncSnapshotMutation> Mutations => _mutations;
}

public sealed record SyncProvisionRequest(
    string PipelineId,
    ChangeSourceIdentity Source,
    SyncTransformVersion Transform);

public sealed record SyncProvisionResult(
    SyncProvisionStatus Status,
    string? ExistingTransformFingerprint = null);

public sealed record SyncApplyResult(
    SyncApplyStatus Status,
    BlueTuskLogSequenceNumber? DurablePosition,
    string? Detail = null)
{
    public static SyncApplyResult Applied(BlueTuskLogSequenceNumber position) =>
        new(SyncApplyStatus.Applied, position);

    public static SyncApplyResult AlreadyApplied(BlueTuskLogSequenceNumber position) =>
        new(SyncApplyStatus.AlreadyApplied, position);
}

public interface ISyncTransform
{
    SyncTransformVersion Version { get; }

    ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
        ChangeTransaction transaction,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
        ChangeSnapshotBatch batch,
        CancellationToken cancellationToken = default);
}

public interface ISyncDestination
{
    string Name { get; }

    SyncDestinationCapabilities Capabilities { get; }

    ValueTask<SyncProvisionResult> ProvisionAsync(
        SyncProvisionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask ResetSnapshotAsync(
        string pipelineId,
        SnapshotReset reset,
        CancellationToken cancellationToken = default);

    ValueTask StartSnapshotAsync(
        string pipelineId,
        SnapshotStart start,
        SyncTransformVersion transform,
        CancellationToken cancellationToken = default);

    ValueTask ApplySnapshotBatchAsync(
        SyncSnapshotBatch batch,
        CancellationToken cancellationToken = default);

    ValueTask CompleteSnapshotAsync(
        string pipelineId,
        SnapshotComplete complete,
        SyncTransformVersion transform,
        CancellationToken cancellationToken = default);

    ValueTask<SyncApplyResult> ApplyTransactionAsync(
        SyncTransactionBatch batch,
        CancellationToken cancellationToken = default);
}

public sealed record SyncQuarantineRecord(
    string PipelineId,
    SyncTransformVersion Transform,
    ChangeSourceIdentity Source,
    uint TransactionId,
    BlueTuskLogSequenceNumber CommitEndPosition,
    string ErrorType,
    string ErrorMessage,
    DateTimeOffset RecordedAt);

public interface ISyncQuarantineSink
{
    ValueTask<bool> StoreAsync(
        SyncQuarantineRecord record,
        CancellationToken cancellationToken = default);
}

public sealed class SyncPoisonRecordException : Exception
{
    public SyncPoisonRecordException(string message)
        : base(message)
    {
    }

    public SyncPoisonRecordException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class SyncTransformVersionMismatchException : Exception
{
    public SyncTransformVersionMismatchException(string current, string requested)
        : base($"The destination transform fingerprint '{current}' does not match requested fingerprint '{requested}'. An explicit rebuild or migration is required.")
    {
        CurrentFingerprint = current;
        RequestedFingerprint = requested;
    }

    public string CurrentFingerprint { get; }

    public string RequestedFingerprint { get; }
}

public sealed class SyncDestinationDurabilityException : Exception
{
    public SyncDestinationDurabilityException(string message)
        : base(message)
    {
    }
}

using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync;

/// <summary>Uniquely identifies one quarantined source transaction.</summary>
public sealed record SyncQuarantineIdentity(
    string PipelineId,
    ChangeSourceIdentity Source,
    uint TransactionId,
    BlueTuskLogSequenceNumber CommitEndPosition)
{
    public static SyncQuarantineIdentity FromRecord(SyncQuarantineRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new SyncQuarantineIdentity(
            record.PipelineId,
            record.Source,
            record.TransactionId,
            record.CommitEndPosition);
    }
}

/// <summary>Describes durable quarantine state without exposing source payload data.</summary>
public sealed record SyncQuarantineEntry(
    SyncQuarantineIdentity Identity,
    string TransformFingerprint,
    string ErrorType,
    string ErrorMessage,
    DateTimeOffset RecordedAt,
    string? ResolvedOperationId,
    DateTimeOffset? ResolvedAt);

public enum SyncQuarantineResolutionStatus
{
    Resolved,
    AlreadyResolved,
    NotFound,
    Conflict,
}

public sealed record SyncQuarantineResolutionResult(
    SyncQuarantineResolutionStatus Status,
    SyncQuarantineEntry? Current);

/// <summary>Persists, reads, and compare-and-set resolves quarantine records.</summary>
public interface ISyncQuarantineStore : ISyncQuarantineSink
{
    ValueTask<SyncQuarantineEntry?> ReadAsync(
        SyncQuarantineIdentity identity,
        CancellationToken cancellationToken = default);

    ValueTask<SyncQuarantineResolutionResult> ResolveAsync(
        SyncQuarantineIdentity identity,
        string expectedTransformFingerprint,
        string operationId,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads an exact retained source transaction for operator replay.</summary>
public interface ISyncQuarantineReplaySource
{
    ValueTask<ChangeTransaction?> ReadTransactionAsync(
        SyncQuarantineIdentity identity,
        CancellationToken cancellationToken = default);
}

public enum SyncQuarantineReplayApplyStatus
{
    Applied,
    AlreadyApplied,
    CheckpointAdvanced,
}

public sealed record SyncQuarantineReplayApplyResult(
    SyncQuarantineReplayApplyStatus Status,
    BlueTuskLogSequenceNumber? CurrentPosition = null);

/// <summary>Applies a replay only when doing so cannot regress later destination state.</summary>
public interface ISyncQuarantineReplayDestination
{
    ValueTask<SyncQuarantineReplayApplyResult> ReplayTransactionAsync(
        SyncTransactionBatch batch,
        string operationId,
        CancellationToken cancellationToken = default);
}

public sealed record SyncQuarantineReplayRequest
{
    public required SyncQuarantineIdentity Identity { get; init; }

    public required string ExpectedTransformFingerprint { get; init; }

    public required string OperationId { get; init; }
}

public enum SyncQuarantineReplayStatus
{
    Completed,
    AlreadyCompleted,
    NotFound,
    SourceTransactionUnavailable,
    CheckpointAdvanced,
}

public sealed record SyncQuarantineReplayResult(
    SyncQuarantineReplayStatus Status,
    BlueTuskLogSequenceNumber? CurrentPosition = null);

/// <summary>Coordinates crash-safe quarantine replay and resolution.</summary>
public sealed class SyncQuarantineReplayCoordinator
{
    private const int MaximumOperationIdLength = 128;
    private readonly ISyncTransform _transform;
    private readonly ISyncQuarantineStore _store;
    private readonly ISyncQuarantineReplaySource _source;
    private readonly ISyncQuarantineReplayDestination _destination;
    private readonly TimeProvider _timeProvider;

    public SyncQuarantineReplayCoordinator(
        ISyncTransform transform,
        ISyncQuarantineStore store,
        ISyncQuarantineReplaySource source,
        ISyncQuarantineReplayDestination destination,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        _transform = transform;
        _store = store;
        _source = source;
        _destination = destination;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<SyncQuarantineReplayResult> ReplayAsync(
        SyncQuarantineReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var entry = await _store.ReadAsync(request.Identity, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return new SyncQuarantineReplayResult(SyncQuarantineReplayStatus.NotFound);
        }

        if (entry.Identity != request.Identity)
        {
            throw new SyncDestinationDurabilityException(
                "The quarantine store returned a record with a different identity.");
        }

        if (entry.ResolvedOperationId is not null)
        {
            return new SyncQuarantineReplayResult(SyncQuarantineReplayStatus.AlreadyCompleted);
        }

        EnsureFingerprint(entry.TransformFingerprint, request.ExpectedTransformFingerprint);
        EnsureFingerprint(_transform.Version.Fingerprint, request.ExpectedTransformFingerprint);
        var transaction = await _source.ReadTransactionAsync(request.Identity, cancellationToken)
            .ConfigureAwait(false);
        if (transaction is null)
        {
            return new SyncQuarantineReplayResult(
                SyncQuarantineReplayStatus.SourceTransactionUnavailable);
        }

        EnsureTransaction(request.Identity, transaction);
        var mutations = await _transform.TransformTransactionAsync(transaction, cancellationToken)
            .ConfigureAwait(false) ??
            throw new SyncPoisonRecordException("The replay transform returned null transaction mutations.");
        var batch = new SyncTransactionBatch(
            request.Identity.PipelineId,
            _transform.Version,
            transaction,
            mutations);
        var apply = await _destination.ReplayTransactionAsync(
            batch,
            request.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (apply.Status is SyncQuarantineReplayApplyStatus.CheckpointAdvanced)
        {
            return new SyncQuarantineReplayResult(
                SyncQuarantineReplayStatus.CheckpointAdvanced,
                apply.CurrentPosition);
        }

        if (apply.Status is not (
                SyncQuarantineReplayApplyStatus.Applied or
                SyncQuarantineReplayApplyStatus.AlreadyApplied))
        {
            throw new SyncDestinationDurabilityException(
                $"The replay destination returned unsupported status '{apply.Status}'.");
        }

        var resolution = await _store.ResolveAsync(
            request.Identity,
            request.ExpectedTransformFingerprint,
            request.OperationId,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        return resolution.Status switch
        {
            SyncQuarantineResolutionStatus.Resolved =>
                new SyncQuarantineReplayResult(SyncQuarantineReplayStatus.Completed),
            SyncQuarantineResolutionStatus.AlreadyResolved =>
                new SyncQuarantineReplayResult(SyncQuarantineReplayStatus.AlreadyCompleted),
            SyncQuarantineResolutionStatus.NotFound =>
                throw new SyncDestinationDurabilityException(
                    "The quarantine record disappeared after durable replay application."),
            SyncQuarantineResolutionStatus.Conflict =>
                throw new SyncDestinationDurabilityException(
                    "The quarantine record changed while replay resolution was being persisted."),
            _ => throw new SyncDestinationDurabilityException(
                $"The quarantine store returned unsupported status '{resolution.Status}'."),
        };
    }

    private static void Validate(SyncQuarantineReplayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Identity.PipelineId);
        ArgumentNullException.ThrowIfNull(request.Identity.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedTransformFingerprint);
        if (request.ExpectedTransformFingerprint.Length != 64 ||
            !request.ExpectedTransformFingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "The expected transform fingerprint must be a 64-character hexadecimal value.",
                nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        if (request.OperationId.Length > MaximumOperationIdLength)
        {
            throw new ArgumentException(
                $"The replay operation identifier cannot exceed {MaximumOperationIdLength} characters.",
                nameof(request));
        }
    }

    private static void EnsureFingerprint(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncTransformVersionMismatchException(actual, expected);
        }
    }

    private static void EnsureTransaction(
        SyncQuarantineIdentity identity,
        ChangeTransaction transaction)
    {
        if (transaction.Source != identity.Source ||
            transaction.TransactionId != identity.TransactionId ||
            transaction.CommitEndPosition != identity.CommitEndPosition)
        {
            throw new SyncDestinationDurabilityException(
                "The replay source returned a transaction that does not match the quarantine identity.");
        }
    }
}

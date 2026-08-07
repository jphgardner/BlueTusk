namespace BlueTusk.Streams;

public readonly record struct ChangeStreamStateKey(
    string SourceFingerprint,
    string ConsumerGroup)
{
    public static ChangeStreamStateKey Create(ChangeSourceIdentity source, string consumerGroup)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        return new ChangeStreamStateKey(source.Fingerprint, consumerGroup);
    }
}

public sealed record ChangeStreamCheckpoint
{
    public const int CurrentFormatVersion = 1;

    public ChangeStreamCheckpoint(
        int formatVersion,
        ChangeSourceIdentity source,
        string databaseIdentity,
        string outputPlugin,
        string mappingFingerprint,
        BlueTuskLogSequenceNumber acknowledgedCommitPosition,
        long storeGeneration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(formatVersion);

        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPlugin);
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingFingerprint);
        ArgumentOutOfRangeException.ThrowIfLessThan(storeGeneration, -1);

        FormatVersion = formatVersion;
        Source = source;
        DatabaseIdentity = databaseIdentity;
        OutputPlugin = outputPlugin;
        MappingFingerprint = mappingFingerprint;
        AcknowledgedCommitPosition = acknowledgedCommitPosition;
        StoreGeneration = storeGeneration;
    }

    public int FormatVersion { get; }

    public ChangeSourceIdentity Source { get; }

    public string DatabaseIdentity { get; }

    public string OutputPlugin { get; }

    public string MappingFingerprint { get; }

    public BlueTuskLogSequenceNumber AcknowledgedCommitPosition { get; }

    public long StoreGeneration { get; }

    public static ChangeStreamCheckpoint CreateInitial(
        ChangeSourceIdentity source,
        string databaseIdentity,
        string outputPlugin,
        string mappingFingerprint) =>
        new(
            CurrentFormatVersion,
            source,
            databaseIdentity,
            outputPlugin,
            mappingFingerprint,
            BlueTuskLogSequenceNumber.Zero,
            storeGeneration: -1);

    public ChangeStreamCheckpoint MoveTo(
        BlueTuskLogSequenceNumber position,
        long storeGeneration)
    {
        if (position < AcknowledgedCommitPosition)
        {
            throw new InvalidOperationException("A change-stream checkpoint cannot move backwards.");
        }

        return new ChangeStreamCheckpoint(
            FormatVersion,
            Source,
            DatabaseIdentity,
            OutputPlugin,
            MappingFingerprint,
            position,
            storeGeneration);
    }

    public void EnsureCompatibleWith(ChangeStreamCheckpoint expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (FormatVersion != expected.FormatVersion ||
            !Equals(Source, expected.Source) ||
            !string.Equals(DatabaseIdentity, expected.DatabaseIdentity, StringComparison.Ordinal) ||
            !string.Equals(OutputPlugin, expected.OutputPlugin, StringComparison.Ordinal) ||
            !string.Equals(MappingFingerprint, expected.MappingFingerprint, StringComparison.Ordinal))
        {
            throw new ChangeStreamCheckpointMismatchException(
                "The checkpoint belongs to a different source, slot, publication, output plug-in, database, mapping, or format.");
        }
    }
}

public sealed class ChangeStreamCheckpointMismatchException : Exception
{
    public ChangeStreamCheckpointMismatchException(string message)
        : base(message)
    {
    }
}

public enum ChangeCheckpointWriteStatus
{
    Stored,
    Conflict,
    BackwardMovement,
    Fenced,
    Incompatible,
}

public sealed record ChangeCheckpointWriteResult(
    ChangeCheckpointWriteStatus Status,
    ChangeStreamCheckpoint? Current);

public interface IChangeCheckpointStore
{
    ValueTask<ChangeStreamCheckpoint?> ReadAsync(
        ChangeStreamStateKey key,
        CancellationToken cancellationToken = default);

    ValueTask<ChangeCheckpointWriteResult> CompareExchangeAsync(
        ChangeStreamStateKey key,
        long expectedGeneration,
        ChangeStreamCheckpoint replacement,
        ChangeStreamLease lease,
        CancellationToken cancellationToken = default);
}

public sealed record ChangeStreamLease(
    ChangeStreamStateKey Key,
    string OwnerId,
    long FencingToken,
    DateTimeOffset ExpiresAt);

public enum ChangeLeaseAcquireStatus
{
    Acquired,
    HeldByAnotherOwner,
}

public sealed record ChangeLeaseAcquireResult(
    ChangeLeaseAcquireStatus Status,
    ChangeStreamLease? Lease);

public interface IChangeStreamLeaseStore
{
    ValueTask<ChangeLeaseAcquireResult> AcquireAsync(
        ChangeStreamStateKey key,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    ValueTask<ChangeStreamLease?> RenewAsync(
        ChangeStreamLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ReleaseAsync(
        ChangeStreamLease lease,
        CancellationToken cancellationToken = default);
}

public interface IChangeStreamStateStore : IChangeCheckpointStore, IChangeStreamLeaseStore;

public sealed class MemoryChangeStreamStateStore : IChangeStreamStateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ChangeStreamStateKey, ChangeStreamCheckpoint> _checkpoints = [];
    private readonly Dictionary<ChangeStreamStateKey, ChangeStreamLease> _leases = [];
    private readonly Dictionary<ChangeStreamStateKey, long> _lastFencingTokens = [];
    private readonly TimeProvider _timeProvider;

    public MemoryChangeStreamStateStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<ChangeStreamCheckpoint?> ReadAsync(
        ChangeStreamStateKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_checkpoints.GetValueOrDefault(key));
        }
    }

    public ValueTask<ChangeCheckpointWriteResult> CompareExchangeAsync(
        ChangeStreamStateKey key,
        long expectedGeneration,
        ChangeStreamCheckpoint replacement,
        ChangeStreamLease lease,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            var current = _checkpoints.GetValueOrDefault(key);
            if (!IsLeaseCurrent(key, lease))
            {
                return ValueTask.FromResult(
                    new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Fenced, current));
            }

            if (!string.Equals(key.SourceFingerprint, replacement.Source.Fingerprint, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(
                    new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Incompatible, current));
            }

            if ((current?.StoreGeneration ?? -1) != expectedGeneration ||
                replacement.StoreGeneration != checked(expectedGeneration + 1))
            {
                return ValueTask.FromResult(
                    new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Conflict, current));
            }

            if (current is not null)
            {
                try
                {
                    replacement.EnsureCompatibleWith(current);
                }
                catch (ChangeStreamCheckpointMismatchException)
                {
                    return ValueTask.FromResult(
                        new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Incompatible, current));
                }

                if (replacement.AcknowledgedCommitPosition < current.AcknowledgedCommitPosition)
                {
                    return ValueTask.FromResult(
                        new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.BackwardMovement, current));
                }
            }

            _checkpoints[key] = replacement;
            return ValueTask.FromResult(
                new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Stored, replacement));
        }
    }

    public ValueTask<ChangeLeaseAcquireResult> AcquireAsync(
        ChangeStreamStateKey key,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLeaseArguments(ownerId, duration);
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_leases.TryGetValue(key, out var current) &&
                current.ExpiresAt > now &&
                !string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(
                    new ChangeLeaseAcquireResult(ChangeLeaseAcquireStatus.HeldByAnotherOwner, current));
            }

            if (current is not null &&
                current.ExpiresAt > now &&
                string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal))
            {
                var renewed = current with { ExpiresAt = now + duration };
                _leases[key] = renewed;
                return ValueTask.FromResult(
                    new ChangeLeaseAcquireResult(ChangeLeaseAcquireStatus.Acquired, renewed));
            }

            var token = checked(_lastFencingTokens.GetValueOrDefault(key) + 1);
            _lastFencingTokens[key] = token;
            var lease = new ChangeStreamLease(key, ownerId, token, now + duration);
            _leases[key] = lease;
            return ValueTask.FromResult(
                new ChangeLeaseAcquireResult(ChangeLeaseAcquireStatus.Acquired, lease));
        }
    }

    public ValueTask<ChangeStreamLease?> RenewAsync(
        ChangeStreamLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLeaseArguments(lease.OwnerId, duration);
        lock (_gate)
        {
            if (!IsLeaseCurrent(lease.Key, lease))
            {
                return ValueTask.FromResult<ChangeStreamLease?>(null);
            }

            var renewed = lease with { ExpiresAt = _timeProvider.GetUtcNow() + duration };
            _leases[lease.Key] = renewed;
            return ValueTask.FromResult<ChangeStreamLease?>(renewed);
        }
    }

    public ValueTask<bool> ReleaseAsync(
        ChangeStreamLease lease,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!IsLeaseCurrent(lease.Key, lease, requireUnexpired: false))
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(_leases.Remove(lease.Key));
        }
    }

    private bool IsLeaseCurrent(
        ChangeStreamStateKey key,
        ChangeStreamLease lease,
        bool requireUnexpired = true) =>
        lease.Key == key &&
        _leases.TryGetValue(key, out var current) &&
        current.FencingToken == lease.FencingToken &&
        string.Equals(current.OwnerId, lease.OwnerId, StringComparison.Ordinal) &&
        (!requireUnexpired || current.ExpiresAt > _timeProvider.GetUtcNow());

    private static void ValidateLeaseArguments(string ownerId, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
    }
}

public interface IReplicationFeedbackSender
{
    ValueTask SendFeedbackAsync(
        BlueTuskLogSequenceNumber position,
        CancellationToken cancellationToken = default);
}

public sealed class LogicalReplicationFeedbackSender : IReplicationFeedbackSender
{
    private readonly BlueTusk.Replication.BlueTuskReplicationConnection _connection;

    public LogicalReplicationFeedbackSender(
        BlueTusk.Replication.BlueTuskReplicationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    public ValueTask SendFeedbackAsync(
        BlueTuskLogSequenceNumber position,
        CancellationToken cancellationToken = default) =>
        _connection.SendStandbyStatusUpdateAsync(
            new BlueTusk.Replication.BlueTuskStandbyStatus(position, position, position),
            cancellationToken);
}

public sealed class CheckpointingChangeDeliveryObserver : IChangeDeliveryObserver, IAsyncDisposable
{
    private readonly IChangeStreamStateStore _store;
    private readonly ChangeStreamStateKey _key;
    private readonly ChangeStreamCheckpoint _identity;
    private readonly TimeSpan _leaseDuration;
    private readonly IReplicationFeedbackSender _feedback;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChangeStreamCheckpoint? _checkpoint;
    private ChangeStreamLease _lease;
    private int _disposed;

    private CheckpointingChangeDeliveryObserver(
        IChangeStreamStateStore store,
        ChangeStreamStateKey key,
        ChangeStreamCheckpoint identity,
        TimeSpan leaseDuration,
        IReplicationFeedbackSender feedback,
        ChangeStreamCheckpoint? checkpoint,
        ChangeStreamLease lease)
    {
        _store = store;
        _key = key;
        _identity = identity;
        _leaseDuration = leaseDuration;
        _feedback = feedback;
        _checkpoint = checkpoint;
        _lease = lease;
    }

    public ChangeStreamCheckpoint? Checkpoint => Volatile.Read(ref _checkpoint);

    public ChangeStreamLease Lease => _lease;

    public static async ValueTask<CheckpointingChangeDeliveryObserver> AcquireAsync(
        IChangeStreamStateStore store,
        ChangeStreamStateKey key,
        string ownerId,
        TimeSpan leaseDuration,
        ChangeStreamCheckpoint identity,
        IReplicationFeedbackSender feedback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(feedback);
        if (!string.Equals(key.SourceFingerprint, identity.Source.Fingerprint, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The state key does not identify the supplied change source.",
                nameof(key));
        }

        var acquired = await store.AcquireAsync(key, ownerId, leaseDuration, cancellationToken)
            .ConfigureAwait(false);
        if (acquired.Status != ChangeLeaseAcquireStatus.Acquired || acquired.Lease is null)
        {
            throw new ChangeStreamLeaseUnavailableException(
                $"Consumer group '{key.ConsumerGroup}' is already owned by '{acquired.Lease?.OwnerId}'.");
        }

        try
        {
            var checkpoint = await store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (checkpoint is not null)
            {
                identity.EnsureCompatibleWith(checkpoint);
            }

            return new CheckpointingChangeDeliveryObserver(
                store,
                key,
                identity,
                leaseDuration,
                feedback,
                checkpoint,
                acquired.Lease);
        }
        catch
        {
            _ = await store.ReleaseAsync(acquired.Lease, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask AcknowledgeAsync(
        ChangeTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var renewed = await _store.RenewAsync(_lease, _leaseDuration, cancellationToken)
                .ConfigureAwait(false);
            _lease = renewed ?? throw new ChangeStreamLeaseLostException(
                $"The lease for consumer group '{_key.ConsumerGroup}' was lost.");

            var current = _checkpoint;
            if (current is not null &&
                transaction.CommitEndPosition <= current.AcknowledgedCommitPosition)
            {
                await _feedback.SendFeedbackAsync(
                        current.AcknowledgedCommitPosition,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var expectedGeneration = current?.StoreGeneration ?? -1;
            var basis = current ?? _identity;
            var replacement = basis.MoveTo(
                transaction.CommitEndPosition,
                checked(expectedGeneration + 1));
            var result = await _store.CompareExchangeAsync(
                    _key,
                    expectedGeneration,
                    replacement,
                    _lease,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Status != ChangeCheckpointWriteStatus.Stored || result.Current is null)
            {
                throw new ChangeStreamCheckpointWriteException(result.Status, result.Current);
            }

            _checkpoint = result.Current;
            await _feedback.SendFeedbackAsync(transaction.CommitEndPosition, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask NackAsync(
        ChangeTransaction transaction,
        Exception? failure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _ = await _store.ReleaseAsync(_lease).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

public sealed class ChangeStreamLeaseUnavailableException : Exception
{
    public ChangeStreamLeaseUnavailableException(string message)
        : base(message)
    {
    }
}

public sealed class ChangeStreamLeaseLostException : Exception
{
    public ChangeStreamLeaseLostException(string message)
        : base(message)
    {
    }
}

public sealed class ChangeStreamCheckpointWriteException : Exception
{
    public ChangeStreamCheckpointWriteException(
        ChangeCheckpointWriteStatus status,
        ChangeStreamCheckpoint? current)
        : base($"The change-stream checkpoint write failed with status {status}.")
    {
        Status = status;
        Current = current;
    }

    public ChangeCheckpointWriteStatus Status { get; }

    public ChangeStreamCheckpoint? Current { get; }
}

public enum ChangeStreamSlotOwnershipMode
{
    DirectDedicatedSlot,
    DurableRelay,
}

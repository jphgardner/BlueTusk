using System.Runtime.CompilerServices;
using BlueTusk.Replication.PgOutput;

namespace BlueTusk.Streams;

public enum PreparedTransactionMode
{
    Fail,
    Stage,
}

public sealed record TransactionAssemblyOptions
{
    public long MaxInMemoryTransactionBytes { get; init; } = 4L * 1024 * 1024;

    public long MaxTransactionBytes { get; init; } = 1024L * 1024 * 1024;

    public long MaxSpoolBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    public int MaxChangesPerTransaction { get; init; } = 1_000_000;

    public int MaxRelationsPerTransaction { get; init; } = 4096;

    public PreparedTransactionMode PreparedTransactionMode { get; init; } =
        PreparedTransactionMode.Fail;

    public string SpoolDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "bluetusk-streams-spool");

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxInMemoryTransactionBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTransactionBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSpoolBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxChangesPerTransaction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRelationsPerTransaction);
        if (!Enum.IsDefined(PreparedTransactionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(PreparedTransactionMode));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(SpoolDirectory);
        if (MaxInMemoryTransactionBytes > MaxTransactionBytes)
        {
            throw new ArgumentException(
                "The in-memory transaction limit cannot exceed the total transaction limit.",
                nameof(MaxInMemoryTransactionBytes));
        }
    }
}

public class TransactionAssemblyException : Exception
{
    public TransactionAssemblyException(string message)
        : base(message)
    {
    }
}

public sealed class TransactionAssemblyLimitExceededException : TransactionAssemblyException
{
    public TransactionAssemblyLimitExceededException(string message)
        : base(message)
    {
    }
}

public sealed class PreparedTransactionNotSupportedException : TransactionAssemblyException
{
    public PreparedTransactionNotSupportedException()
        : base(
            "Prepared and two-phase transactions require TransactionAssemblyOptions.PreparedTransactionMode " +
            "to be Stage so consumers explicitly opt in to durable staging semantics.")
    {
    }
}

public interface IChangeStream
{
    IAsyncEnumerable<ChangeTransactionDelivery> ReadTransactionsAsync(
        CancellationToken cancellationToken = default);
}

public interface IChangeDeliveryObserver
{
    ValueTask AcknowledgeAsync(
        ChangeTransaction transaction,
        CancellationToken cancellationToken = default);

    ValueTask NackAsync(
        ChangeTransaction transaction,
        Exception? failure,
        CancellationToken cancellationToken = default);
}

public enum ChangeDeliveryState
{
    Active,
    Acknowledged,
    Nacked,
    Disposed,
}

public sealed class ChangeTransactionDelivery : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask> _acknowledge;
    private readonly Func<Exception?, CancellationToken, ValueTask> _nack;
    private readonly string[]? _replicationPublicationNames;
    private readonly uint? _replicationTimeline;
    private readonly uint? _replicationDatabaseOid;
    private readonly uint? _replicationPublicationOid;
    private readonly long _telemetryStarted;
    private int _state;

    public ChangeTransactionDelivery(
        ChangeTransaction transaction,
        IChangeDeliveryObserver observer)
        : this(
            transaction,
            cancellationToken => observer.AcknowledgeAsync(transaction, cancellationToken),
            (failure, cancellationToken) =>
                observer.NackAsync(transaction, failure, cancellationToken))
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(observer);
    }

    internal ChangeTransactionDelivery(
        ChangeTransaction transaction,
        Func<CancellationToken, ValueTask> acknowledge,
        Func<Exception?, CancellationToken, ValueTask> nack)
        : this(transaction, acknowledge, nack, null)
    {
    }

    internal ChangeTransactionDelivery(
        ChangeTransaction transaction,
        Func<CancellationToken, ValueTask> acknowledge,
        Func<Exception?, CancellationToken, ValueTask> nack,
        IReadOnlyList<string>? replicationPublicationNames)
        : this(transaction, acknowledge, nack, replicationPublicationNames, null)
    {
    }

    internal ChangeTransactionDelivery(
        ChangeTransaction transaction,
        Func<CancellationToken, ValueTask> acknowledge,
        Func<Exception?, CancellationToken, ValueTask> nack,
        IReadOnlyList<string>? replicationPublicationNames,
        uint? replicationTimeline)
        : this(transaction, acknowledge, nack, replicationPublicationNames, replicationTimeline, null, null)
    {
    }

    internal ChangeTransactionDelivery(
        ChangeTransaction transaction,
        Func<CancellationToken, ValueTask> acknowledge,
        Func<Exception?, CancellationToken, ValueTask> nack,
        IReadOnlyList<string>? replicationPublicationNames,
        uint? replicationTimeline,
        uint? replicationDatabaseOid,
        uint? replicationPublicationOid)
    {
        Transaction = transaction;
        _acknowledge = acknowledge;
        _nack = nack;
        _replicationPublicationNames = replicationPublicationNames?.ToArray();
        _replicationTimeline = replicationTimeline;
        _replicationDatabaseOid = replicationDatabaseOid;
        _replicationPublicationOid = replicationPublicationOid;
        _telemetryStarted = BlueTuskStreamsDiagnostics.StartDelivery(transaction);
    }

    public ChangeTransaction Transaction { get; }

    // Only the built-in replication source can supply this evidence in production.
    // A caller-created delivery or generic decoded envelope has no publication binding.
    internal bool HasSingleReplicationPublication(string publicationName) =>
        _replicationPublicationNames is { Length: 1 } names &&
        string.Equals(names[0], publicationName, StringComparison.Ordinal);

    internal bool HasSingleReplicationPublicationOnTimeline(string publicationName, long timeline) =>
        HasSingleReplicationPublication(publicationName) &&
        _replicationTimeline.HasValue && _replicationTimeline.Value > 0 &&
        _replicationTimeline.Value == timeline;

    internal bool HasSingleReplicationPublicationOnLineage(string publicationName, long timeline,
        uint databaseOid, uint publicationOid) =>
        HasSingleReplicationPublicationOnTimeline(publicationName, timeline) &&
        databaseOid != 0 && publicationOid != 0 &&
        _replicationDatabaseOid == databaseOid && _replicationPublicationOid == publicationOid;

    public ChangeDeliveryState State => Volatile.Read(ref _state) switch
    {
        0 or 1 => ChangeDeliveryState.Active,
        2 => ChangeDeliveryState.Acknowledged,
        3 => ChangeDeliveryState.Nacked,
        4 => ChangeDeliveryState.Disposed,
        _ => throw new InvalidOperationException("The change delivery has an invalid state."),
    };

    public async ValueTask AcknowledgeAsync(CancellationToken cancellationToken = default)
    {
        BeginSettlement();
        try
        {
            await _acknowledge(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _state, 2);
            BlueTuskStreamsDiagnostics.RecordDeliverySettlement(
                Transaction,
                "acknowledged",
                _telemetryStarted);
        }
        catch
        {
            Volatile.Write(ref _state, 0);
            BlueTuskStreamsDiagnostics.RecordDeliverySettlementFailure(
                Transaction,
                "acknowledge");
            throw;
        }
    }

    public async ValueTask NackAsync(Exception? error = null, CancellationToken cancellationToken = default)
    {
        BeginSettlement();
        try
        {
            await _nack(error, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _state, 3);
            BlueTuskStreamsDiagnostics.RecordDeliverySettlement(
                Transaction,
                "nacked",
                _telemetryStarted);
        }
        catch
        {
            Volatile.Write(ref _state, 0);
            BlueTuskStreamsDiagnostics.RecordDeliverySettlementFailure(
                Transaction,
                "nack");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _nack(null, CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref _state, 4);
            BlueTuskStreamsDiagnostics.RecordDeliverySettlement(
                Transaction,
                "disposed",
                _telemetryStarted);
        }
        catch
        {
            Volatile.Write(ref _state, 0);
            BlueTuskStreamsDiagnostics.RecordDeliverySettlementFailure(
                Transaction,
                "dispose");
            throw;
        }
    }

    private void BeginSettlement()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException("A change delivery can be settled only once.");
        }
    }
}

public sealed class ChangeDeliveryNotAcknowledgedException : Exception
{
    public ChangeDeliveryNotAcknowledgedException(ChangeDeliveryState state)
        : base($"The previous change transaction was not acknowledged; its final state is {state}.")
    {
        State = state;
    }

    public ChangeDeliveryState State { get; }
}

public sealed class PgOutputChangeStream : IChangeStream
{
    private readonly IAsyncEnumerable<BlueTuskPgOutputEnvelope> _source;
    private readonly PgOutputTransactionAssembler _assembler;
    private readonly IChangeDeliveryObserver _observer;
    private readonly string[]? _replicationPublicationNames;
    private readonly uint? _replicationTimeline;
    private readonly uint? _replicationDatabaseOid;
    private readonly uint? _replicationPublicationOid;
    private int _started;

    public PgOutputChangeStream(
        IAsyncEnumerable<BlueTuskPgOutputEnvelope> source,
        ChangeSourceIdentity sourceIdentity,
        TransactionAssemblyOptions? options = null,
        ITransactionSpool? spool = null,
        IChangeDeliveryObserver? observer = null)
        : this(source, sourceIdentity, options, spool, observer, null)
    {
    }

    internal PgOutputChangeStream(
        IAsyncEnumerable<BlueTuskPgOutputEnvelope> source,
        ChangeSourceIdentity sourceIdentity,
        TransactionAssemblyOptions? options,
        ITransactionSpool? spool,
        IChangeDeliveryObserver? observer,
        IReadOnlyList<string>? replicationPublicationNames,
        uint? replicationTimeline = null,
        uint? replicationDatabaseOid = null,
        uint? replicationPublicationOid = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        var effectiveOptions = options ?? new TransactionAssemblyOptions();
        effectiveOptions.Validate();
        _source = source;
        _observer = observer ?? NullChangeDeliveryObserver.Instance;
        _replicationPublicationNames = replicationPublicationNames?.ToArray();
        _replicationTimeline = replicationTimeline;
        _replicationDatabaseOid = replicationDatabaseOid;
        _replicationPublicationOid = replicationPublicationOid;
        _assembler = new PgOutputTransactionAssembler(
            sourceIdentity,
            effectiveOptions,
            spool ?? new FileTransactionSpool(
                new FileTransactionSpoolOptions
                {
                    DirectoryPath = effectiveOptions.SpoolDirectory,
                    MaxStorageBytes = effectiveOptions.MaxSpoolBytes,
                    MaxRecordBytes = checked((int)Math.Min(effectiveOptions.MaxTransactionBytes, int.MaxValue)),
                }));
    }

    public async IAsyncEnumerable<ChangeTransactionDelivery> ReadTransactionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("A pgoutput change stream can be consumed only once.");
        }

        ChangeTransactionDelivery? outstanding = null;
        try
        {
            await foreach (var envelope in _source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var assembled = await _assembler.ProcessAsync(envelope, cancellationToken).ConfigureAwait(false);
                if (assembled is null)
                {
                    continue;
                }

                outstanding = CreateDelivery(assembled, envelope.XLogData.Origin);
                BlueTuskStreamsDiagnostics.RecordTransaction(assembled.Transaction);
                yield return outstanding;
                if (outstanding.State != ChangeDeliveryState.Acknowledged)
                {
                    var state = outstanding.State;
                    await outstanding.DisposeAsync().ConfigureAwait(false);
                    throw new ChangeDeliveryNotAcknowledgedException(state);
                }

                outstanding = null;
            }
        }
        finally
        {
            if (outstanding is not null)
            {
                await outstanding.DisposeAsync().ConfigureAwait(false);
            }

            await _assembler.AbortAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private ChangeTransactionDelivery CreateDelivery(
        AssembledChangeTransaction assembled,
        BlueTusk.Replication.BlueTuskReplicationConnection? origin)
    {
        // An attached observer owns position reporting: it may first persist a checkpoint that a
        // later resume validates against the slot. Without one, acknowledgement is the only
        // completion signal, so the stream confirms the commit position on the WAL sender that
        // delivered it. Otherwise the slot's confirmed_flush_lsn never moves and PostgreSQL
        // retains WAL indefinitely.
        var confirmOnAcknowledge = _observer is NullChangeDeliveryObserver ? origin : null;
        return new(
            assembled.Transaction,
            async cancellationToken =>
            {
                await _observer.AcknowledgeAsync(assembled.Transaction, cancellationToken).ConfigureAwait(false);
                if (confirmOnAcknowledge is not null)
                {
                    await confirmOnAcknowledge.AdvanceStandbyStatusAsync(
                            assembled.Transaction.CommitEndPosition,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await assembled.ReleaseAsync().ConfigureAwait(false);
            },
            async (failure, cancellationToken) =>
            {
                await _observer.NackAsync(assembled.Transaction, failure, cancellationToken).ConfigureAwait(false);
                await assembled.ReleaseAsync().ConfigureAwait(false);
            },
            _replicationPublicationNames,
            _replicationTimeline,
            _replicationDatabaseOid,
            _replicationPublicationOid);
    }

    private sealed class NullChangeDeliveryObserver : IChangeDeliveryObserver
    {
        public static NullChangeDeliveryObserver Instance { get; } = new();

        public ValueTask AcknowledgeAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask NackAsync(
            ChangeTransaction transaction,
            Exception? failure,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}

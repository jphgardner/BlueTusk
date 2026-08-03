using System.Runtime.CompilerServices;
using BlueTusk.Replication.PgOutput;

namespace BlueTusk.Streams;

public sealed record TransactionAssemblyOptions
{
    public long MaxInMemoryTransactionBytes { get; init; } = 4L * 1024 * 1024;

    public long MaxTransactionBytes { get; init; } = 1024L * 1024 * 1024;

    public long MaxSpoolBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    public int MaxChangesPerTransaction { get; init; } = 1_000_000;

    public int MaxRelationsPerTransaction { get; init; } = 4096;

    public string SpoolDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "bluetusk-streams-spool");

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxInMemoryTransactionBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTransactionBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSpoolBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxChangesPerTransaction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRelationsPerTransaction);
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
        : base("Prepared and two-phase transactions are reserved for the Streams hardening phase.")
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
    private int _state;

    internal ChangeTransactionDelivery(
        ChangeTransaction transaction,
        Func<CancellationToken, ValueTask> acknowledge,
        Func<Exception?, CancellationToken, ValueTask> nack)
    {
        Transaction = transaction;
        _acknowledge = acknowledge;
        _nack = nack;
    }

    public ChangeTransaction Transaction { get; }

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
        }
        catch
        {
            Volatile.Write(ref _state, 0);
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
        }
        catch
        {
            Volatile.Write(ref _state, 0);
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
        }
        catch
        {
            Volatile.Write(ref _state, 0);
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
    private int _started;

    public PgOutputChangeStream(
        IAsyncEnumerable<BlueTuskPgOutputEnvelope> source,
        ChangeSourceIdentity sourceIdentity,
        TransactionAssemblyOptions? options = null,
        ITransactionSpool? spool = null,
        IChangeDeliveryObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        var effectiveOptions = options ?? new TransactionAssemblyOptions();
        effectiveOptions.Validate();
        _source = source;
        _observer = observer ?? NullChangeDeliveryObserver.Instance;
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

                outstanding = CreateDelivery(assembled);
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

    private ChangeTransactionDelivery CreateDelivery(AssembledChangeTransaction assembled) =>
        new(
            assembled.Transaction,
            async cancellationToken =>
            {
                await _observer.AcknowledgeAsync(assembled.Transaction, cancellationToken).ConfigureAwait(false);
                await assembled.ReleaseAsync().ConfigureAwait(false);
            },
            async (failure, cancellationToken) =>
            {
                await _observer.NackAsync(assembled.Transaction, failure, cancellationToken).ConfigureAwait(false);
                await assembled.ReleaseAsync().ConfigureAwait(false);
            });

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

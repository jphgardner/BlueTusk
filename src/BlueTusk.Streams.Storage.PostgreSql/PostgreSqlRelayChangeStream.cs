using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace BlueTusk.Streams.Storage.PostgreSql;

/// <summary>Controls bounded delivery from one durable PostgreSQL relay consumer group.</summary>
public sealed record PostgreSqlRelayChangeStreamOptions
{
    /// <summary>Gets the independently checkpointed relay consumer-group name.</summary>
    public required string ConsumerGroup { get; init; }

    /// <summary>Gets the unique owner identity used to acquire the fenced group lease.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Gets where a newly created consumer group begins.</summary>
    public ChangeRelayConsumerGroupStart NewGroupStart { get; init; } =
        ChangeRelayConsumerGroupStart.EarliestAvailable;

    /// <summary>Gets the maximum number of transactions materialized by one relay read.</summary>
    public int MaxTransactionsPerRead { get; init; } = 128;

    /// <summary>Gets the maximum encoded bytes materialized by one relay read.</summary>
    public long MaxBytesPerRead { get; init; } = 8L * 1024 * 1024;

    /// <summary>Gets the delay before polling an empty relay group again.</summary>
    public TimeSpan EmptyReadDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Gets the fenced lease duration.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how often the active lease is renewed.</summary>
    public TimeSpan LeaseRenewalInterval { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(OwnerId);
        if (!Enum.IsDefined(NewGroupStart))
        {
            throw new ArgumentOutOfRangeException(nameof(NewGroupStart));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTransactionsPerRead);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBytesPerRead);
        ArgumentOutOfRangeException.ThrowIfLessThan(EmptyReadDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LeaseDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LeaseRenewalInterval, TimeSpan.Zero);
        if (LeaseRenewalInterval >= LeaseDuration)
        {
            throw new ArgumentException(
                "The relay lease renewal interval must be shorter than the lease duration.",
                nameof(LeaseRenewalInterval));
        }
    }
}

/// <summary>
/// Reads transaction-preserving deliveries from one independently checkpointed relay group.
/// </summary>
public sealed class PostgreSqlRelayChangeStream : IChangeStream
{
    private readonly PostgreSqlDurableChangeRelay _relay;
    private readonly ChangeRelaySourceRegistration _source;
    private readonly PostgreSqlRelayChangeStreamOptions _options;
    private int _started;

    /// <summary>Initializes a single-use durable relay stream.</summary>
    public PostgreSqlRelayChangeStream(
        PostgreSqlDurableChangeRelay relay,
        ChangeRelaySourceRegistration source,
        PostgreSqlRelayChangeStreamOptions options)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChangeTransactionDelivery> ReadTransactionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "A PostgreSQL relay change stream can be consumed only once.");
        }

        await _relay.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var group = await _relay.CreateConsumerGroupAsync(
            _source,
            _options.ConsumerGroup,
            _options.NewGroupStart,
            cancellationToken).ConfigureAwait(false);
        var lease = await _relay.AcquireConsumerGroupAsync(
            group,
            _options.OwnerId,
            _options.LeaseDuration,
            cancellationToken).ConfigureAwait(false) ??
            throw new ChangeRelayLeaseUnavailableException(
                $"Relay consumer group '{group.Name}' is already owned by another worker.");
        await using var session = new ConsumerGroupSession(
            _relay,
            lease,
            group.StoreGeneration,
            _options.LeaseDuration,
            _options.LeaseRenewalInterval,
            cancellationToken);

        ChangeTransactionDelivery? outstanding = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                session.ThrowIfLeaseLost();
                var batch = await _relay.ReadConsumerGroupAsync(
                    session.Lease,
                    _options.MaxTransactionsPerRead,
                    _options.MaxBytesPerRead,
                    cancellationToken).ConfigureAwait(false);
                session.SynchronizeGeneration(batch.Group.StoreGeneration);
                if (batch.Records.Count == 0)
                {
                    await Task.Delay(_options.EmptyReadDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var record in batch.Records)
                {
                    session.ThrowIfLeaseLost();
                    outstanding = new ChangeTransactionDelivery(
                        record.Transaction,
                        new ConsumerGroupDeliveryObserver(session, record.Sequence));
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
        }
        finally
        {
            if (outstanding is not null)
            {
                await outstanding.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class ConsumerGroupDeliveryObserver(
        ConsumerGroupSession session,
        long sequence) : IChangeDeliveryObserver
    {
        public ValueTask AcknowledgeAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default) =>
            session.AcknowledgeAsync(sequence, cancellationToken);

        public ValueTask NackAsync(
            ChangeTransaction transaction,
            Exception? failure,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConsumerGroupSession : IAsyncDisposable
    {
        private readonly PostgreSqlDurableChangeRelay _relay;
        private readonly TimeSpan _leaseDuration;
        private readonly CancellationTokenSource _renewalCancellation;
        private readonly Task _renewal;
        private ChangeRelayGroupLease _lease;
        private ExceptionDispatchInfo? _leaseFailure;
        private long _generation;
        private int _disposed;

        public ConsumerGroupSession(
            PostgreSqlDurableChangeRelay relay,
            ChangeRelayGroupLease lease,
            long generation,
            TimeSpan leaseDuration,
            TimeSpan renewalInterval,
            CancellationToken cancellationToken)
        {
            _relay = relay;
            _lease = lease;
            _generation = generation;
            _leaseDuration = leaseDuration;
            _renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _renewal = RenewLeaseAsync(renewalInterval, _renewalCancellation.Token);
        }

        public ChangeRelayGroupLease Lease => Volatile.Read(ref _lease);

        public void SynchronizeGeneration(long generation)
        {
            var current = Interlocked.Read(ref _generation);
            if (generation != current)
            {
                throw new ChangeRelayConsumerGroupException(
                    $"Relay consumer-group generation changed from {current} to {generation} outside this delivery session.");
            }
        }

        public void ThrowIfLeaseLost() => Volatile.Read(ref _leaseFailure)?.Throw();

        public async ValueTask AcknowledgeAsync(
            long sequence,
            CancellationToken cancellationToken)
        {
            ThrowIfLeaseLost();
            var expectedGeneration = Interlocked.Read(ref _generation);
            var result = await _relay.AcknowledgeConsumerGroupAsync(
                Lease,
                expectedGeneration,
                sequence,
                cancellationToken).ConfigureAwait(false);
            if (result.Status is not ChangeRelayAcknowledgeStatus.Stored)
            {
                throw new ChangeRelayConsumerGroupException(
                    $"Relay consumer-group acknowledgement for sequence {sequence} failed with status '{result.Status}'.");
            }

            Interlocked.Exchange(ref _generation, result.Current.StoreGeneration);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _renewalCancellation.Cancel();
            try
            {
                await _renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_renewalCancellation.IsCancellationRequested)
            {
            }

            _renewalCancellation.Dispose();
            _ = await _relay.ReleaseConsumerGroupAsync(Lease).ConfigureAwait(false);
        }

        private async Task RenewLeaseAsync(
            TimeSpan interval,
            CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                    var renewed = await _relay.RenewConsumerGroupAsync(
                        Lease,
                        _leaseDuration,
                        cancellationToken).ConfigureAwait(false) ??
                        throw new ChangeRelayLeaseLostException(
                            "The relay consumer-group lease was lost during renewal.");
                    Volatile.Write(ref _lease, renewed);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _leaseFailure, ExceptionDispatchInfo.Capture(exception));
            }
        }
    }
}

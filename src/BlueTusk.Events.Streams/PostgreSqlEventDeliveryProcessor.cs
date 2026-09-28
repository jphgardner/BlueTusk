using System.Data;
using System.Data.Common;
using BlueTusk.Streams;

namespace BlueTusk.Events.Streams;

public sealed class EventStreamsDeliveryOptions
{
    public int MaximumSourceChanges { get; init; } = 100_000;
    public int MaximumEvents { get; init; } = 1024;
    public int MaximumPayloadBytes { get; init; } = 8_388_608;
}

public sealed record EventStreamsDeliveryResult(int OutboxEvents, int HandledEvents);

/// <summary>Explicit target incarnation and source lineage for durable published-retention ACKs.</summary>
public sealed record EventPublishedRetentionTargetOptions(Guid TargetIncarnation, EventPublishedSourceIdentity Source);

/// <summary>
/// Commit a complete source transaction's business events and target inbox effects, then acknowledge
/// its Streams delivery. Streams owns source checkpointing; the target inbox makes redelivery idempotent.
/// </summary>
public sealed class PostgreSqlEventDeliveryProcessor
{
    private readonly DbDataSource _targetDataSource;
    private readonly PostgreSqlEventStore _inbox;
    private readonly EventOutboxChangeDecoder _decoder;
    private readonly string _consumerId;
    private readonly EventStreamsDeliveryOptions _options;
    private readonly ChangeSourceIdentity _source;
    private readonly EventPublishedRetentionTargetOptions? _protectedRetention;
    private readonly EventPublishedRetentionChangeDecoder _retentionDecoder;

    public PostgreSqlEventDeliveryProcessor(DbDataSource targetDataSource, PostgreSqlEventStore inbox,
        EventOutboxChangeDecoder decoder, string consumerId, ChangeSourceIdentity source,
        EventStreamsDeliveryOptions? options = null)
        : this(targetDataSource, inbox, decoder, consumerId, source, options, null)
    {
    }

    public static PostgreSqlEventDeliveryProcessor CreateProtected(DbDataSource targetDataSource,
        PostgreSqlEventStore inbox, EventOutboxChangeDecoder decoder, string consumerId,
        ChangeSourceIdentity source, EventStreamsDeliveryOptions? options,
        EventPublishedRetentionTargetOptions protectedRetention)
    {
        ArgumentNullException.ThrowIfNull(protectedRetention);
        return new PostgreSqlEventDeliveryProcessor(targetDataSource, inbox, decoder, consumerId,
            source, options, protectedRetention);
    }

    private PostgreSqlEventDeliveryProcessor(DbDataSource targetDataSource, PostgreSqlEventStore inbox,
        EventOutboxChangeDecoder decoder, string consumerId, ChangeSourceIdentity source,
        EventStreamsDeliveryOptions? options, EventPublishedRetentionTargetOptions? protectedRetention)
    {
        ArgumentNullException.ThrowIfNull(targetDataSource);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        if (consumerId.Length > 200 || consumerId.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Consumer identity must contain 1 to 200 characters and no NUL characters.", nameof(consumerId));
        }

        _options = options ?? new EventStreamsDeliveryOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumSourceChanges);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumEvents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaximumEvents, 65_536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaximumPayloadBytes, 67_108_864);
        _targetDataSource = targetDataSource;
        _inbox = inbox;
        _decoder = decoder;
        _consumerId = consumerId;
        _source = source;
        if (protectedRetention is not null &&
            (protectedRetention.TargetIncarnation == Guid.Empty || protectedRetention.Source is null ||
             protectedRetention.Source.SystemIdentifier != source.SystemIdentifier ||
             protectedRetention.Source.DatabaseName != source.DatabaseName ||
             protectedRetention.Source.SlotName != source.SlotName))
        {
            throw new ArgumentException("Protected retention must bind this ordered source and a target incarnation.", nameof(protectedRetention));
        }
        _protectedRetention = protectedRetention;
        _retentionDecoder = new EventPublishedRetentionChangeDecoder(decoder.Schema);
    }

    public async ValueTask<EventStreamsDeliveryResult> ProcessAsync(ChangeTransactionDelivery delivery,
        EventTransactionHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(handler);
        var transaction = delivery.Transaction;
        if (delivery.State != ChangeDeliveryState.Active || transaction.Source != _source ||
            transaction.Outcome != ChangeTransactionOutcome.Committed || transaction.IsSynthetic || transaction.IsTwoPhase)
        {
            throw new ArgumentException("Event delivery requires an active raw committed transaction from the registered Streams source.", nameof(delivery));
        }
        if (_protectedRetention is not null &&
            !delivery.HasSingleReplicationPublication(_protectedRetention.Source.PublicationName))
        {
            throw new InvalidOperationException("Protected retention requires a verified single-publication replication delivery from the registered source publication.");
        }

        if (transaction.Changes.Count > _options.MaximumSourceChanges)
        {
            throw new InvalidOperationException("The event delivery exceeds the configured source change bound.");
        }

        var events = new List<StoredEvent>();
        var positions = new Dictionary<EventStreamKey, long>();
        var identities = new HashSet<(string Tenant, Guid Identity)>();
        PublishedRetentionControl? control = null;
        long bytes = 0;
        var ordinal = 0;
        await foreach (var change in transaction.Changes.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (change is not InsertChange and not UpdateChange and not DeleteChange and not TruncateChange and not LogicalMessageChange)
            {
                throw new InvalidOperationException("Business event delivery requires raw Streams change contracts.");
            }

            if (change.Id.Source != transaction.Source || change.Id.TransactionId != transaction.TransactionId ||
                change.Id.CommitEndPosition != transaction.CommitEndPosition || change.Id.Ordinal != ordinal++ ||
                ordinal > _options.MaximumSourceChanges)
            {
                throw new InvalidOperationException("Event source changes did not retain their ordered transaction identity.");
            }

            if (_retentionDecoder.TryDecode(change, out var decodedControl))
            {
                if (_protectedRetention is null || control is not null || decodedControl.Source != _protectedRetention.Source)
                {
                    throw new InvalidOperationException("The ordered retention control has no matching protected target or is duplicated.");
                }
                control = decodedControl;
                continue;
            }

            if (!_decoder.TryDecode(change, out var value))
            {
                continue;
            }

            bytes += value.Payload.Length;
            if (events.Count == _options.MaximumEvents || bytes > _options.MaximumPayloadBytes)
            {
                throw new InvalidOperationException("The source transaction's business events exceed the configured event count or payload byte bound.");
            }

            if (!identities.Add((value.Stream.TenantId, value.EventId)) ||
                positions.TryGetValue(value.Stream, out var previous) && value.Sequence <= previous)
            {
                throw new InvalidOperationException("Business events within a source transaction must have unique tenant identities and increasing stream sequences.");
            }

            positions[value.Stream] = value.Sequence;
            events.Add(value);
        }

        if (ordinal != transaction.Changes.Count)
        {
            throw new InvalidOperationException("Event source changes did not match the declared change count.");
        }
        if (control is not null && ordinal != 1)
        {
            throw new InvalidOperationException("A retention intent must be the only raw change in its ordered source transaction.");
        }

        var handled = 0;
        if (events.Count > 0 || _protectedRetention is not null)
        {
            await using var connection = await _targetDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var targetTransaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            if (_protectedRetention is not null)
            {
                await _inbox.LockPublishedRetentionDeliveryAsync(connection, targetTransaction, _consumerId,
                    _protectedRetention.TargetIncarnation, _protectedRetention.Source,
                    transaction.CommitEndPosition.Value, transaction.TransactionId, control,
                    cancellationToken).ConfigureAwait(false);
            }
            foreach (var value in events)
            {
                if (await _inbox.ProcessInboxAsync(connection, targetTransaction, _consumerId, value, handler, cancellationToken).ConfigureAwait(false))
                {
                    handled++;
                }
            }

            if (control is not null)
            {
                await _inbox.AcknowledgePublishedRetentionControlAsync(connection, targetTransaction, _consumerId,
                    _protectedRetention!.TargetIncarnation, _protectedRetention.Source,
                    transaction.CommitEndPosition.Value, control, cancellationToken).ConfigureAwait(false);
            }

            await targetTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Failed/ambiguous acknowledgment leaves committed inbox effects intact. Redelivery deduplicates
        // them, while handler/commit failures leave the source delivery active and target effects rolled back.
        await delivery.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
        return new EventStreamsDeliveryResult(events.Count, handled);
    }
}

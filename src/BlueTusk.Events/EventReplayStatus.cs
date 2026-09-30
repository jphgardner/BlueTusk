namespace BlueTusk.Events;

/// <summary>One committed metadata snapshot. Ownership is observed at database time, not permission to perform future work.</summary>
public sealed class EventReplayStatus
{
    internal EventReplayStatus(string consumerId, EventStreamKey stream, bool streamExists, bool replayExists,
        long streamHead, long checkpoint, string? ownerId, long fencingToken, DateTimeOffset? leaseExpiresAt, DateTimeOffset observedAt)
    {
        ConsumerId = consumerId; Stream = stream; StreamExists = streamExists; ReplayExists = replayExists;
        StreamHead = streamHead; Checkpoint = checkpoint; OwnerId = ownerId; FencingToken = fencingToken;
        LeaseExpiresAt = leaseExpiresAt; ObservedAt = observedAt;
    }
    public string ConsumerId { get; }
    public EventStreamKey Stream { get; }
    public bool StreamExists { get; }
    public bool ReplayExists { get; }
    public long StreamHead { get; }
    public long Checkpoint { get; }
    public long RemainingEvents => StreamHead - Checkpoint;
    public string? OwnerId { get; }
    public long FencingToken { get; }
    public DateTimeOffset? LeaseExpiresAt { get; }
    public DateTimeOffset ObservedAt { get; }
    public bool IsLeaseActive => OwnerId is not null && LeaseExpiresAt > ObservedAt;
}

public sealed partial class PostgreSqlEventStore
{
    /// <summary>
    /// Reads the indexed stream head and consumer checkpoint/lease together without creating rows or reading outbox/inbox payloads.
    /// RemainingEvents is exact because committed stream sequences are gap-free; this is a health observation, not lease admission.
    /// </summary>
    public async ValueTask<EventReplayStatus> ReadReplayStatusAsync(string consumerId, EventStreamKey stream, CancellationToken cancellationToken = default)
    {
        EventValidation.Key(consumerId, nameof(consumerId));
        ArgumentNullException.ThrowIfNull(stream);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            WITH observed AS MATERIALIZED(SELECT clock_timestamp() AS observed_at)
            SELECT s.tenant_id IS NOT NULL,r.consumer_id IS NOT NULL,COALESCE(s.last_sequence,0),COALESCE(r.checkpoint,0),
                CASE WHEN octet_length(r.owner_id)<=800 AND length(r.owner_id)<=200 THEN r.owner_id ELSE NULL END,
                COALESCE(r.fencing_token,0),r.expires_at,observed.observed_at,
                COALESCE(octet_length(r.owner_id)>800 OR length(r.owner_id)>200,false)
            FROM observed
            LEFT JOIN {_schema}.streams s ON s.tenant_id=@tenant AND s.stream_id=@stream
            LEFT JOIN {_schema}.replay r ON r.consumer_id=@consumer AND r.tenant_id=@tenant AND r.stream_id=@stream
            """, ("consumer", consumerId), ("tenant", stream.TenantId), ("stream", stream.StreamId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { throw new InvalidOperationException("The event replay metadata snapshot is absent."); }
        var head = reader.GetInt64(2); var checkpoint = reader.GetInt64(3);
        if (checkpoint > head || head < 0 || checkpoint < 0 || reader.GetBoolean(1) && !reader.GetBoolean(0))
        { throw new InvalidOperationException("Event replay metadata violates the committed gap-free stream/checkpoint invariant."); }
        if (reader.GetBoolean(8)) { throw new InvalidOperationException("Event replay owner metadata exceeds its bounded identity contract."); }
        var result = new EventReplayStatus(consumerId, stream, reader.GetBoolean(0), reader.GetBoolean(1), head, checkpoint,
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5),
            reader.IsDBNull(6) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)));
        EventsDiagnostics.ReplayObserved(result.RemainingEvents);
        return result;
    }
}

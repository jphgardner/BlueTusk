using System.Data;
using System.Data.Common;

namespace BlueTusk.Events;

/// <summary>An immutable event to append within an application's transaction.</summary>
public sealed class EventWrite
{
    public EventWrite(Guid eventId, string eventType, int version, DateTimeOffset occurredAt, ReadOnlySpan<byte> payload)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("An event identity must be nonempty and stable across retries.", nameof(eventId));
        }

        EventValidation.Key(eventType, nameof(eventType));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (payload.IsEmpty)
        {
            throw new ArgumentException("Event payloads must be nonempty.", nameof(payload));
        }

        EventId = eventId;
        EventType = eventType;
        Version = version;
        OccurredAt = occurredAt.ToUniversalTime();
        Payload = payload.ToArray();
    }

    public Guid EventId { get; }
    public string EventType { get; }
    public int Version { get; }
    public DateTimeOffset OccurredAt { get; }
    public ReadOnlyMemory<byte> Payload { get; }
}

public sealed record EventStreamKey
{
    public EventStreamKey(string tenantId, string streamId)
    {
        EventValidation.Key(tenantId, nameof(tenantId));
        EventValidation.Key(streamId, nameof(streamId));
        TenantId = tenantId;
        StreamId = streamId;
    }

    public string TenantId { get; }
    public string StreamId { get; }
}

public sealed record StoredEvent(EventStreamKey Stream, long Sequence, Guid EventId, string EventType,
    int Version, DateTimeOffset OccurredAt, ReadOnlyMemory<byte> Payload);

public sealed record EventAppendReceipt(Guid EventId, long Sequence, bool WasAlreadyStored);

public sealed record EventReplayLease(string ConsumerId, EventStreamKey Stream, string OwnerId, long FencingToken);

public sealed record EventReplayResult(int HandledCount, long Checkpoint, bool ReachedEnd);

public sealed class EventIdentityConflictException : InvalidOperationException
{
    public EventIdentityConflictException(Guid eventId)
        : base($"Event identity {eventId} was already used with a different stream or event content.") => EventId = eventId;

    public Guid EventId { get; }
}

public sealed class EventReplayFencedException : InvalidOperationException
{
    public EventReplayFencedException() : base("The replay lease has expired or has been replaced. The transaction must be rolled back.") { EventsDiagnostics.LeaseFenced(); }
}

/// <summary>Handlers must use the supplied connection and transaction for all database effects.</summary>
public delegate ValueTask EventTransactionHandler(StoredEvent value, DbConnection connection,
    DbTransaction transaction, CancellationToken cancellationToken);

public sealed class PostgreSqlEventsOptions
{
    public string Schema { get; init; } = "bluetusk_events";
    public int MaximumAppendEvents { get; init; } = 1024;
    public int MaximumEventBytes { get; init; } = 1_048_576;
    public int MaximumAppendBytes { get; init; } = 8_388_608;
    public int CommandTimeoutSeconds { get; init; } = 30;
    /// <summary>Explicitly permit operator-certified retention of local-only, unpublished outboxes.</summary>
    public bool EnableLocalOnlyRetention { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Schema);
        if (Schema.Length > 63 || Schema.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
            !char.IsAsciiLetter(Schema[0]) && Schema[0] != '_')
        {
            throw new ArgumentException("Schema must be a PostgreSQL identifier of 1 to 63 ASCII letters, digits, or underscores.", nameof(Schema));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAppendEvents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumAppendEvents, 65_536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEventBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumEventBytes, 16_777_216);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumAppendBytes, MaximumEventBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumAppendBytes, 67_108_864);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CommandTimeoutSeconds);
    }
}

internal static class EventValidation
{
    internal static void Key(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 200 || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Event keys must contain 1 to 200 characters and no NUL characters.", parameterName);
        }
    }

    internal static void Transaction(DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException("An open connection and its active transaction are required.", nameof(transaction));
        }
    }
}

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace BlueTusk.Live;

public sealed class LiveReplayEvent
{
    private readonly byte[] _integrityHash;
    private readonly ReadOnlyMemory<byte> _payload;

    public LiveReplayEvent(
        long sequence,
        LiveEventKind kind,
        string contentType,
        ReadOnlySpan<byte> payload)
        : this(sequence, kind, contentType, payload.ToArray())
    {
    }

    private LiveReplayEvent(
        long sequence,
        LiveEventKind kind,
        string contentType,
        byte[] ownedPayload)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        Sequence = sequence;
        Kind = kind;
        ContentType = contentType;
        _payload = ownedPayload;
        _integrityHash = SHA256.HashData(ownedPayload);
    }

    public long Sequence { get; }

    public LiveEventKind Kind { get; }

    public string ContentType { get; }

    public ReadOnlyMemory<byte> Payload => _payload;

    public ReadOnlyMemory<byte> IntegrityHash => _integrityHash;

    internal static LiveReplayEvent CreateOwned(
        long sequence,
        LiveEventKind kind,
        string contentType,
        byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new LiveReplayEvent(sequence, kind, contentType, payload);
    }

    public static LiveReplayEvent Restore(
        long sequence,
        LiveEventKind kind,
        string contentType,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> integrityHash)
    {
        var restored = new LiveReplayEvent(sequence, kind, contentType, payload);
        if (integrityHash.Length != SHA256.HashSizeInBytes ||
            !CryptographicOperations.FixedTimeEquals(restored._integrityHash, integrityHash))
        {
            throw new ArgumentException("The stored Live replay payload does not match its integrity hash.", nameof(integrityHash));
        }

        return restored;
    }
}

public sealed class LiveReplayAppendRequest
{
    private readonly ReadOnlyCollection<LiveReplayEvent> _events;

    public LiveReplayAppendRequest(
        LiveSubscriptionIdentity identity,
        long expectedLastSequence,
        IEnumerable<LiveReplayEvent> events)
        : this(identity, expectedLastSequence, MaterializeEvents(events))
    {
    }

    private LiveReplayAppendRequest(
        LiveSubscriptionIdentity identity,
        long expectedLastSequence,
        LiveReplayEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLastSequence);
        if (events.Length == 0)
        {
            throw new ArgumentException("A Live replay append requires at least one event.", nameof(events));
        }

        for (var index = 0; index < events.Length; index++)
        {
            var expected = checked(expectedLastSequence + index + 1);
            if (events[index].Sequence != expected)
            {
                throw new ArgumentException(
                    $"Live replay sequence {events[index].Sequence} is not the expected sequence {expected}.",
                    nameof(events));
            }
        }

        Identity = identity;
        ExpectedLastSequence = expectedLastSequence;
        _events = Array.AsReadOnly(events);
    }

    public LiveSubscriptionIdentity Identity { get; }

    public long ExpectedLastSequence { get; }

    public IReadOnlyList<LiveReplayEvent> Events => _events;

    internal static LiveReplayAppendRequest CreateOwned(
        LiveSubscriptionIdentity identity,
        long expectedLastSequence,
        LiveReplayEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return new LiveReplayAppendRequest(identity, expectedLastSequence, events);
    }

    private static LiveReplayEvent[] MaterializeEvents(IEnumerable<LiveReplayEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events.ToArray();
    }
}

public enum LiveReplayAppendStatus
{
    Stored,
    AlreadyStored,
    SequenceConflict,
}

public sealed record LiveReplayAppendResult(
    LiveReplayAppendStatus Status,
    long CurrentLastSequence);

public enum LiveReplayReadStatus
{
    Available,
    Current,
    Expired,
    NotFound,
}

public sealed class LiveReplayReadResult
{
    private readonly ReadOnlyCollection<LiveReplayEvent> _events;

    public LiveReplayReadResult(
        LiveReplayReadStatus status,
        long firstAvailableSequence,
        long lastSequence,
        IEnumerable<LiveReplayEvent>? events = null)
        : this(status, firstAvailableSequence, lastSequence, events?.ToArray() ?? [])
    {
    }

    private LiveReplayReadResult(
        LiveReplayReadStatus status,
        long firstAvailableSequence,
        long lastSequence,
        LiveReplayEvent[] events)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(firstAvailableSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(lastSequence);
        if (firstAvailableSequence > checked(lastSequence + 1))
        {
            throw new ArgumentException("The first available replay sequence cannot exceed the last sequence plus one.");
        }

        Status = status;
        FirstAvailableSequence = firstAvailableSequence;
        LastSequence = lastSequence;
        _events = Array.AsReadOnly(events);
    }

    public LiveReplayReadStatus Status { get; }

    public long FirstAvailableSequence { get; }

    public long LastSequence { get; }

    public IReadOnlyList<LiveReplayEvent> Events => _events;

    internal static LiveReplayReadResult CreateOwned(
        LiveReplayReadStatus status,
        long firstAvailableSequence,
        long lastSequence,
        LiveReplayEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return new LiveReplayReadResult(status, firstAvailableSequence, lastSequence, events);
    }
}

public interface ILiveReplayStore
{
    ValueTask<LiveReplayAppendResult> AppendAsync(
        LiveReplayAppendRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<LiveReplayReadResult> ReadAsync(
        LiveSubscriptionIdentity identity,
        long afterSequence,
        int maximumEvents,
        CancellationToken cancellationToken = default);

    ValueTask<int> PruneAsync(CancellationToken cancellationToken = default);
}

public static class LiveReplayJsonSerializer
{
    public const int CurrentFormatVersion = 1;

    public const int MinimumSupportedFormatVersion = 1;

    public const string ContentType = "application/vnd.bluetusk.live-event+json;v=1";

    public static LiveReplayEvent Serialize<T, TKey>(
        LiveResultEvent<T, TKey> liveEvent,
        JsonSerializerOptions? options = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        return LiveReplayEvent.CreateOwned(
            liveEvent.Sequence,
            liveEvent.Kind,
            ContentType,
            JsonSerializer.SerializeToUtf8Bytes(liveEvent, options));
    }

    public static bool VerifyIntegrity(LiveReplayEvent replayEvent)
    {
        ArgumentNullException.ThrowIfNull(replayEvent);
        var computed = SHA256.HashData(replayEvent.Payload.Span);
        return CryptographicOperations.FixedTimeEquals(computed, replayEvent.IntegrityHash.Span);
    }
}

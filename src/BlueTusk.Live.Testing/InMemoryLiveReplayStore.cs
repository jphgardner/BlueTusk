using System.Security.Cryptography;

namespace BlueTusk.Live.Testing;

public sealed class InMemoryLiveReplayStore : ILiveReplayStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SubscriptionState> _subscriptions = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retention;

    public InMemoryLiveReplayStore(
        TimeSpan? retention = null,
        TimeProvider? timeProvider = null)
    {
        _retention = retention ?? TimeSpan.FromMinutes(30);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_retention, TimeSpan.Zero);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<LiveReplayAppendResult> AppendAsync(
        LiveReplayAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var state = GetOrCreate(request.Identity);
            if (request.ExpectedLastSequence == state.LastSequence)
            {
                var now = _timeProvider.GetUtcNow();
                state.Events.AddRange(request.Events.Select(item => new StoredEvent(item, now)));
                state.LastSequence = request.Events[^1].Sequence;
                return ValueTask.FromResult(new LiveReplayAppendResult(
                    LiveReplayAppendStatus.Stored,
                    state.LastSequence));
            }

            if (IsIdenticalRetry(state, request))
            {
                return ValueTask.FromResult(new LiveReplayAppendResult(
                    LiveReplayAppendStatus.AlreadyStored,
                    state.LastSequence));
            }

            return ValueTask.FromResult(new LiveReplayAppendResult(
                LiveReplayAppendStatus.SequenceConflict,
                state.LastSequence));
        }
    }

    public ValueTask<LiveReplayReadResult> ReadAsync(
        LiveSubscriptionIdentity identity,
        long afterSequence,
        int maximumEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEvents);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(identity.Fingerprint, out var state))
            {
                return ValueTask.FromResult(new LiveReplayReadResult(
                    LiveReplayReadStatus.NotFound,
                    0,
                    0));
            }

            var first = state.Events.Count == 0
                ? checked(state.LastSequence + 1)
                : state.Events[0].Event.Sequence;
            if (afterSequence < first - 1)
            {
                return ValueTask.FromResult(new LiveReplayReadResult(
                    LiveReplayReadStatus.Expired,
                    first,
                    state.LastSequence));
            }

            var events = state.Events
                .Where(item => item.Event.Sequence > afterSequence)
                .Take(maximumEvents)
                .Select(item => item.Event)
                .ToArray();
            return ValueTask.FromResult(new LiveReplayReadResult(
                events.Length == 0 ? LiveReplayReadStatus.Current : LiveReplayReadStatus.Available,
                first,
                state.LastSequence,
                events));
        }
    }

    public ValueTask<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cutoff = _timeProvider.GetUtcNow() - _retention;
        var removed = 0;
        lock (_gate)
        {
            foreach (var state in _subscriptions.Values)
            {
                removed += state.Events.RemoveAll(item => item.StoredAt <= cutoff);
            }
        }

        return ValueTask.FromResult(removed);
    }

    private SubscriptionState GetOrCreate(LiveSubscriptionIdentity identity)
    {
        if (!_subscriptions.TryGetValue(identity.Fingerprint, out var state))
        {
            state = new SubscriptionState(identity);
            _subscriptions.Add(identity.Fingerprint, state);
        }

        return state;
    }

    private static bool IsIdenticalRetry(
        SubscriptionState state,
        LiveReplayAppendRequest request)
    {
        if (request.ExpectedLastSequence >= state.LastSequence)
        {
            return false;
        }

        foreach (var requested in request.Events)
        {
            var stored = state.Events.FirstOrDefault(item => item.Event.Sequence == requested.Sequence);
            if (stored is null ||
                stored.Event.Kind != requested.Kind ||
                !string.Equals(stored.Event.ContentType, requested.ContentType, StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(
                    stored.Event.IntegrityHash.Span,
                    requested.IntegrityHash.Span))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record StoredEvent(LiveReplayEvent Event, DateTimeOffset StoredAt);

    private sealed class SubscriptionState(LiveSubscriptionIdentity identity)
    {
        public LiveSubscriptionIdentity Identity { get; } = identity;

        public long LastSequence { get; set; }

        public List<StoredEvent> Events { get; } = [];
    }
}

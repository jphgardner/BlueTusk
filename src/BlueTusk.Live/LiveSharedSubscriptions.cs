using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace BlueTusk.Live;

public enum LiveSlowClientPolicy
{
    Disconnect,
    RequireReset,
}

public enum LiveSubscriberMessageKind
{
    Event,
    ResetRequired,
}

public sealed record LiveSubscriberMessage(
    LiveSubscriberMessageKind Kind,
    LiveReplayEvent? Event);

public sealed record LiveSharedSubscriptionOptions
{
    public int MaximumSubscribers { get; init; } = 1_000;

    public int SubscriberBufferCapacity { get; init; } = 128;

    public int MaximumReplayEventsPerConnect { get; init; } = 1_024;

    public LiveSlowClientPolicy SlowClientPolicy { get; init; } = LiveSlowClientPolicy.Disconnect;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSubscribers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SubscriberBufferCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumReplayEventsPerConnect);
        if (MaximumReplayEventsPerConnect == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumReplayEventsPerConnect),
                "The replay connect limit must leave room for one overflow-detection event.");
        }
        if (!Enum.IsDefined(SlowClientPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(SlowClientPolicy));
        }
    }
}

public enum LiveSubscriptionConnectStatus
{
    Connected,
    NotStarted,
    QuotaExceeded,
    ReplayUnavailable,
    ReplayLimitExceeded,
    InvalidResumeToken,
    ResumeTokenExpired,
}

public sealed record LiveSharedSubscriptionStatus(
    LiveSubscriptionIdentity Identity,
    bool IsStarted,
    int SubscriberCount,
    long PersistedSequence,
    long PublishedEvents,
    long FanOutDeliveries,
    long SlowClientDisconnects,
    LiveQuerySessionStatus QuerySession);

public sealed class LiveSubscriptionConnectResult
{
    internal LiveSubscriptionConnectResult(
        LiveSubscriptionConnectStatus status,
        LiveSubscriptionConnection? connection,
        LiveResumeTokenValidationStatus? tokenStatus = null)
    {
        Status = status;
        Connection = connection;
        TokenStatus = tokenStatus;
    }

    public LiveSubscriptionConnectStatus Status { get; }

    public LiveSubscriptionConnection? Connection { get; }

    public LiveResumeTokenValidationStatus? TokenStatus { get; }
}

public sealed class LiveSubscriptionConnection : IAsyncDisposable
{
    private readonly ChannelReader<LiveSubscriberMessage> _reader;
    private readonly Action<Guid> _onDispose;
    private readonly ReadOnlyCollection<LiveReplayEvent> _replay;
    private int _disposed;

    internal LiveSubscriptionConnection(
        Guid id,
        IEnumerable<LiveReplayEvent> replay,
        ChannelReader<LiveSubscriberMessage> reader,
        Action<Guid> onDispose)
    {
        Id = id;
        _replay = Array.AsReadOnly(replay.ToArray());
        _reader = reader;
        _onDispose = onDispose;
    }

    public Guid Id { get; }

    public IReadOnlyList<LiveReplayEvent> Replay => _replay;

    public IAsyncEnumerable<LiveSubscriberMessage> ReadAllAsync(
        CancellationToken cancellationToken = default) =>
        ReadCoreAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _onDispose(Id);
        }

        return ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<LiveSubscriberMessage> ReadCoreAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in _reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }
}

public interface ILiveSharedSubscription : IAsyncDisposable
{
    LiveSubscriptionIdentity Identity { get; }

    ValueTask<LiveSubscriptionConnectResult> ConnectAsync(
        long afterSequence,
        CancellationToken cancellationToken = default);

    ValueTask<LiveSubscriptionConnectResult> ConnectWithTokenAsync(
        string resumeToken,
        LiveResumeTokenProtector tokenProtector,
        CancellationToken cancellationToken = default);
}

public sealed class LiveSharedSubscription<T, TKey> : ILiveSharedSubscription
    where TKey : notnull
{
    private readonly LiveQuerySession<T, TKey> _session;
    private readonly ILiveReplayStore _replayStore;
    private readonly LiveSharedSubscriptionOptions _options;
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _persistedSequence;
    private long _publishedEvents;
    private long _fanOutDeliveries;
    private long _slowClientDisconnects;
    private int _started;
    private int _disposed;

    public LiveSharedSubscription(
        LiveQuerySession<T, TKey> session,
        ILiveReplayStore replayStore,
        LiveSharedSubscriptionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(replayStore);
        options ??= new LiveSharedSubscriptionOptions();
        options.Validate();
        _session = session;
        _replayStore = replayStore;
        _options = options;
    }

    public LiveSubscriptionIdentity Identity => _session.Identity;

    public LiveSharedSubscriptionStatus Status => new(
        Identity,
        Volatile.Read(ref _started) != 0,
        _subscribers.Count,
        Interlocked.Read(ref _persistedSequence),
        Interlocked.Read(ref _publishedEvents),
        Interlocked.Read(ref _fanOutDeliveries),
        Interlocked.Read(ref _slowClientDisconnects),
        _session.Status);

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _started) != 0)
            {
                throw new InvalidOperationException("A shared Live subscription can be started only once.");
            }

            var initial = await _session.StartAsync(cancellationToken).ConfigureAwait(false);
            await PersistAsync(initial.Events, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _started, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<int> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            var batch = await _session.RefreshToCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (batch is null || batch.Events.Count == 0)
            {
                return 0;
            }

            var replayEvents = await PersistAsync(batch.Events, cancellationToken).ConfigureAwait(false);
            Publish(replayEvents);
            return replayEvents.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LiveSubscriptionConnectResult> ConnectAsync(
        long afterSequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _started) == 0)
            {
                return new LiveSubscriptionConnectResult(LiveSubscriptionConnectStatus.NotStarted, null);
            }

            if (_subscribers.Count >= _options.MaximumSubscribers)
            {
                return new LiveSubscriptionConnectResult(LiveSubscriptionConnectStatus.QuotaExceeded, null);
            }

            var replay = await _replayStore.ReadAsync(
                Identity,
                afterSequence,
                checked(_options.MaximumReplayEventsPerConnect + 1),
                cancellationToken).ConfigureAwait(false);
            if (replay.Status is LiveReplayReadStatus.Expired or LiveReplayReadStatus.NotFound)
            {
                if (afterSequence != 0)
                {
                    return new LiveSubscriptionConnectResult(LiveSubscriptionConnectStatus.ReplayUnavailable, null);
                }

                var reset = await _session.ResetAsync(
                    LiveResetReason.ReplayExpired,
                    cancellationToken).ConfigureAwait(false);
                var resetEvents = await PersistAsync(reset.Events, cancellationToken).ConfigureAwait(false);
                Publish(resetEvents);
                replay = new LiveReplayReadResult(
                    LiveReplayReadStatus.Available,
                    resetEvents[0].Sequence,
                    resetEvents[^1].Sequence,
                    resetEvents);
            }

            if (replay.Events.Count > _options.MaximumReplayEventsPerConnect ||
                (replay.Status is LiveReplayReadStatus.Available &&
                 replay.Events.Count != 0 &&
                 replay.Events[^1].Sequence < replay.LastSequence))
            {
                return new LiveSubscriptionConnectResult(LiveSubscriptionConnectStatus.ReplayLimitExceeded, null);
            }

            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<LiveSubscriberMessage>(new BoundedChannelOptions(
                _options.SubscriberBufferCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
            var subscriber = new Subscriber(channel);
            if (!_subscribers.TryAdd(id, subscriber))
            {
                throw new InvalidOperationException("A generated Live subscriber ID collided unexpectedly.");
            }

            var connection = new LiveSubscriptionConnection(
                id,
                replay.Events,
                channel.Reader,
                RemoveSubscriber);
            return new LiveSubscriptionConnectResult(LiveSubscriptionConnectStatus.Connected, connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LiveSubscriptionConnectResult> ConnectWithTokenAsync(
        string resumeToken,
        LiveResumeTokenProtector tokenProtector,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resumeToken);
        ArgumentNullException.ThrowIfNull(tokenProtector);
        var validation = tokenProtector.Validate(resumeToken, Identity);
        if (validation.Status is LiveResumeTokenValidationStatus.Expired)
        {
            return new LiveSubscriptionConnectResult(
                LiveSubscriptionConnectStatus.ResumeTokenExpired,
                null,
                validation.Status);
        }

        if (validation.Status is not LiveResumeTokenValidationStatus.Valid)
        {
            return new LiveSubscriptionConnectResult(
                LiveSubscriptionConnectStatus.InvalidResumeToken,
                null,
                validation.Status);
        }

        return await ConnectAsync(validation.Position!.Sequence, cancellationToken).ConfigureAwait(false);
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
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Channel.Writer.TryComplete();
            }

            _subscribers.Clear();
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async ValueTask<IReadOnlyList<LiveReplayEvent>> PersistAsync(
        IReadOnlyList<LiveResultEvent<T, TKey>> events,
        CancellationToken cancellationToken)
    {
        var replayEvents = events
            .Select(liveEvent => LiveReplayJsonSerializer.Serialize(liveEvent))
            .ToArray();
        var expected = Interlocked.Read(ref _persistedSequence);
        var result = await _replayStore.AppendAsync(
            new LiveReplayAppendRequest(Identity, expected, replayEvents),
            cancellationToken).ConfigureAwait(false);
        if (result.Status is LiveReplayAppendStatus.SequenceConflict)
        {
            throw new LiveReplaySequenceException(
                $"Live replay sequence for '{Identity.Fingerprint}' is {result.CurrentLastSequence}, expected {expected}.");
        }

        var finalSequence = replayEvents[^1].Sequence;
        if (result.CurrentLastSequence != finalSequence)
        {
            throw new LiveReplaySequenceException(
                $"Live replay append ended at {result.CurrentLastSequence}, not {finalSequence}.");
        }

        Interlocked.Exchange(ref _persistedSequence, finalSequence);
        Interlocked.Add(ref _publishedEvents, replayEvents.Length);
        return replayEvents;
    }

    private void Publish(IReadOnlyList<LiveReplayEvent> events)
    {
        foreach (var (id, subscriber) in _subscribers)
        {
            var active = true;
            foreach (var replayEvent in events)
            {
                if (subscriber.Channel.Writer.TryWrite(
                        new LiveSubscriberMessage(LiveSubscriberMessageKind.Event, replayEvent)))
                {
                    Interlocked.Increment(ref _fanOutDeliveries);
                    continue;
                }

                active = false;
                Interlocked.Increment(ref _slowClientDisconnects);
                if (_options.SlowClientPolicy is LiveSlowClientPolicy.RequireReset)
                {
                    while (subscriber.Channel.Reader.TryRead(out _))
                    {
                    }

                    _ = subscriber.Channel.Writer.TryWrite(
                        new LiveSubscriberMessage(LiveSubscriberMessageKind.ResetRequired, null));
                    subscriber.Channel.Writer.TryComplete();
                }
                else
                {
                    subscriber.Channel.Writer.TryComplete(
                        new LiveSlowClientException(
                            $"Live subscriber '{id}' exceeded its bounded delivery buffer."));
                }

                _subscribers.TryRemove(id, out _);
                break;
            }

            if (!active)
            {
                continue;
            }
        }
    }

    private void RemoveSubscriber(Guid id)
    {
        if (_subscribers.TryRemove(id, out var subscriber))
        {
            subscriber.Channel.Writer.TryComplete();
        }
    }

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _started) == 0)
        {
            throw new InvalidOperationException("The shared Live subscription has not been started.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record Subscriber(Channel<LiveSubscriberMessage> Channel);
}

public sealed record LiveSharedSubscriptionRegistryOptions
{
    public int MaximumSharedSubscriptions { get; init; } = 10_000;

    internal void Validate() => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSharedSubscriptions);
}

public sealed class LiveSharedSubscriptionRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, object> _subscriptions = new(StringComparer.Ordinal);
    private readonly LiveSharedSubscriptionRegistryOptions _options;
    private readonly object _mutationLock = new();
    private int _disposed;

    public LiveSharedSubscriptionRegistry(LiveSharedSubscriptionRegistryOptions? options = null)
    {
        options ??= new LiveSharedSubscriptionRegistryOptions();
        options.Validate();
        _options = options;
    }

    public int Count => _subscriptions.Count;

    public LiveSharedSubscription<T, TKey> GetOrAdd<T, TKey>(
        LiveSharedSubscription<T, TKey> subscription)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_mutationLock)
        {
            if (_subscriptions.TryGetValue(subscription.Identity.Fingerprint, out var existing))
            {
                return existing as LiveSharedSubscription<T, TKey> ??
                    throw new InvalidOperationException(
                        "A shared Live subscription identity was registered with different row or key types.");
            }

            if (_subscriptions.Count >= _options.MaximumSharedSubscriptions)
            {
                throw new LiveSubscriptionQuotaException(
                    $"The shared Live subscription limit of {_options.MaximumSharedSubscriptions} has been reached.");
            }

            if (!_subscriptions.TryAdd(subscription.Identity.Fingerprint, subscription))
            {
                throw new InvalidOperationException("A shared Live subscription was added concurrently outside the registry lock.");
            }

            return subscription;
        }
    }

    public async ValueTask<bool> RemoveAsync(
        LiveSubscriptionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!_subscriptions.TryRemove(identity.Fingerprint, out var subscription))
        {
            return false;
        }

        if (subscription is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var subscription in _subscriptions.Values.OfType<IAsyncDisposable>())
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        _subscriptions.Clear();
    }
}

public class LiveSubscriptionException : LiveQueryException
{
    public LiveSubscriptionException(string message)
        : base(message)
    {
    }

    public LiveSubscriptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class LiveReplaySequenceException : LiveSubscriptionException
{
    public LiveReplaySequenceException(string message)
        : base(message)
    {
    }
}

public sealed class LiveSlowClientException : LiveSubscriptionException
{
    public LiveSlowClientException(string message)
        : base(message)
    {
    }
}

public sealed class LiveSubscriptionQuotaException : LiveSubscriptionException
{
    public LiveSubscriptionQuotaException(string message)
        : base(message)
    {
    }
}

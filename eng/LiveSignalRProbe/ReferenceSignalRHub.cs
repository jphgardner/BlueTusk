using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using BlueTusk.Live;
using BlueTusk.Live.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BlueTusk.LiveSignalRProbe;

[Authorize]
internal sealed class ReferenceSignalRHub(ReferenceResultPublisher publisher) : Hub
{
    public IAsyncEnumerable<ReferenceSignalRMessage> SubscribeAsync(
        LiveSubscriptionRequest request, CancellationToken cancellationToken = default) =>
        publisher.SubscribeAsync(Context.ConnectionId, request, cancellationToken);
}

internal sealed class ReferenceResultPublisher(
    LiveSubscriptionIdentity identity,
    LiveResumeTokenProtector tokenProtector,
    IReadOnlyList<ResultRow> initialRows,
    bool churn, bool testSlowClient)
{
    private readonly ConcurrentDictionary<string, Channel<JsonElement>> _subscribers = new();
    private readonly Queue<JsonElement> _replay = new(SignalRCapture.SlowClientBurstLength);
    private long _slowClientResets;
    private IReadOnlyList<ResultRow> _previous = initialRows;
    private long _sequence = 1;

    public int SubscriberCount => _subscribers.Count;
    public long SlowClientResets => Interlocked.Read(ref _slowClientResets);

    public async IAsyncEnumerable<ReferenceSignalRMessage> SubscribeAsync(
        string connectionId,
        LiveSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = tokenProtector.Validate(request.ResumeToken ?? string.Empty, identity);
        if (request.Query != SignalRCapture.QueryName || request.Parameters.ValueKind != JsonValueKind.Object ||
            cursor.Status != LiveResumeTokenValidationStatus.Valid || cursor.Position is not { } position)
        {
            throw new HubException("The reference stream requires a valid fixture cursor.");
        }

        var capacity = testSlowClient ? SignalRCapture.SlowClientQueueCapacity : churn ? 2 : 1;
        var queue = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(capacity)
        {
            SingleReader = !testSlowClient,
            SingleWriter = true,
        });
        JsonElement[] pending;
        lock (_replay)
        {
            var earliest = testSlowClient ? Math.Max(1, _sequence - SignalRCapture.SlowClientBurstLength) : _sequence;
            if (position.Sequence < earliest || position.Sequence > _sequence)
            {
                throw new HubException("The reference stream no longer retains the requested position.");
            }
            pending = testSlowClient
                ? _replay.Where(update => update.GetProperty("sequence").GetInt64() > position.Sequence).ToArray()
                : [];
            if (!_subscribers.TryAdd(connectionId, queue))
            {
                throw new HubException("Each benchmark client may have one active subscription.");
            }
        }

        try
        {
            foreach (var update in pending)
            {
                yield return ToMessage(update);
            }

            await foreach (var update in queue.Reader.ReadAllAsync(cancellationToken))
            {
                if (update.ValueKind == JsonValueKind.Undefined)
                {
                    yield return new ReferenceSignalRMessage("ResetRequired", null, null, null);
                    yield break;
                }
                yield return ToMessage(update);
            }
        }
        finally
        {
            _subscribers.TryRemove(new KeyValuePair<string, Channel<JsonElement>>(connectionId, queue));
        }
    }

    public void Publish(IReadOnlyList<ResultRow> current)
    {
        if (current.Count != _previous.Count)
        {
            throw new InvalidDataException("The SignalR reference result count changed.");
        }

        if (churn)
        {
            var last = current.Count - 1;
            for (var index = 0; index < last; index++)
            {
                if (current[index] != _previous[index])
                {
                    throw new InvalidDataException("The SignalR churn workload changed a retained row.");
                }
            }
            if (current[last].Id <= _previous[last].Id)
            {
                throw new InvalidDataException("The SignalR churn workload did not replace its final row.");
            }

            var removed = CreateEvent("RowRemoved", _previous[last].Id, null, last, null);
            var added = CreateEvent("RowAdded", current[last].Id, current[last], null, last);
            _previous = current;
            foreach (var subscriber in _subscribers)
            {
                PublishToSubscriber(subscriber, removed);
                PublishToSubscriber(subscriber, added);
            }
            return;
        }

        var changedIndex = -1;
        for (var index = 0; index < current.Count; index++)
        {
            if (current[index].Id != _previous[index].Id)
            {
                throw new InvalidDataException("The SignalR reference result order changed.");
            }
            if (current[index] == _previous[index])
            {
                continue;
            }
            if (changedIndex != -1)
            {
                throw new InvalidDataException("The SignalR reference observed more than one changed row.");
            }
            changedIndex = index;
        }

        if (changedIndex == -1)
        {
            throw new InvalidDataException("The SignalR reference did not observe an update.");
        }

        var changed = current[changedIndex];
        var update = CreateEvent("RowUpdated", changed.Id, changed, changedIndex, changedIndex);
        _previous = current;
        if (testSlowClient)
        {
            lock (_replay)
            {
                _replay.Enqueue(update);
                if (_replay.Count > SignalRCapture.SlowClientBurstLength)
                {
                    _replay.Dequeue();
                }
            }
        }
        foreach (var subscriber in _subscribers)
        {
            PublishToSubscriber(subscriber, update);
        }
    }

    private JsonElement CreateEvent(string kind, int key, ResultRow? row, int? previousIndex, int? currentIndex) =>
        JsonSerializer.SerializeToElement(new
        {
            sequence = ++_sequence,
            kind,
            key,
            row,
            previousIndex,
            currentIndex,
            rows = (object?)null,
            order = (object?)null,
            resetReason = (string?)null,
        });

    private ReferenceSignalRMessage ToMessage(JsonElement update)
    {
        var sequence = update.GetProperty("sequence").GetInt64();
        return new ReferenceSignalRMessage("Event", sequence,
            tokenProtector.Protect(identity, sequence, TimeSpan.FromMinutes(30)), update);
    }

    private void PublishToSubscriber(KeyValuePair<string, Channel<JsonElement>> subscriber, JsonElement update)
    {
        var queue = subscriber.Value;
        if (queue.Writer.TryWrite(update))
        {
            return;
        }
        if (!testSlowClient || !_subscribers.TryRemove(subscriber))
        {
            throw new InvalidDataException("A SignalR reference client fell behind the capture.");
        }
        Interlocked.Increment(ref _slowClientResets);
        while (queue.Reader.TryRead(out _)) { }
        if (!queue.Writer.TryWrite(default))
        {
            throw new InvalidDataException("The reference could not signal that the slow client must reset.");
        }
        queue.Writer.Complete();
    }
}

internal sealed record ResultRow(int Id, int Revision);
internal sealed record ReferenceSignalRMessage(string Kind, long? Sequence, string? ResumeToken, JsonElement? Event);

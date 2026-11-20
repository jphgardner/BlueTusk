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
    bool churn)
{
    private readonly ConcurrentDictionary<string, ChannelWriter<JsonElement>> _writers = new();
    private IReadOnlyList<ResultRow> _previous = initialRows;
    private long _sequence = 1;

    public int SubscriberCount => _writers.Count;

    public async IAsyncEnumerable<ReferenceSignalRMessage> SubscribeAsync(
        string connectionId,
        LiveSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = tokenProtector.Validate(request.ResumeToken ?? string.Empty, identity);
        if (request.Query != SignalRCapture.QueryName || request.Parameters.ValueKind != JsonValueKind.Object ||
            cursor.Status != LiveResumeTokenValidationStatus.Valid || cursor.Position?.Sequence != 1)
        {
            throw new HubException("The reference stream requires the fixture's initial cursor.");
        }

        var queue = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(churn ? 2 : 1)
        {
            SingleReader = true,
            SingleWriter = true,
        });
        if (!_writers.TryAdd(connectionId, queue.Writer))
        {
            throw new HubException("Each benchmark client may subscribe once.");
        }

        try
        {
            await foreach (var update in queue.Reader.ReadAllAsync(cancellationToken))
            {
                var sequence = update.GetProperty("sequence").GetInt64();
                yield return new ReferenceSignalRMessage("Event", sequence,
                    tokenProtector.Protect(identity, sequence, TimeSpan.FromMinutes(30)), update);
            }
        }
        finally
        {
            _writers.TryRemove(connectionId, out _);
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
            foreach (var queue in _writers.Values)
            {
                Write(queue, removed);
                Write(queue, added);
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
        foreach (var queue in _writers.Values)
        {
            Write(queue, update);
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

    private static void Write(ChannelWriter<JsonElement> queue, JsonElement update)
    {
        if (!queue.TryWrite(update))
        {
            throw new InvalidDataException("A SignalR reference client fell behind the capture.");
        }
    }
}

internal sealed record ResultRow(int Id, int Revision);
internal sealed record ReferenceSignalRMessage(string Kind, long Sequence, string ResumeToken, JsonElement Event);

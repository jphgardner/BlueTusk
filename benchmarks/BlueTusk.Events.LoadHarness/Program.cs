using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using BlueTusk.Benchmarks.ExpansionCapacity;
using BlueTusk.Data;

namespace BlueTusk.Events.LoadHarness;

internal static class Program
{
    private static Task<int> Main(string[] args) =>
        CapacityHost.RunAsync(args, "Events", static (context, cancellationToken) => EventsWorkload.CreateAsync(context, cancellationToken));
}

// Sustained Events hot path against the owned PostgreSQL fixture: caller-transaction appends at a
// fixed offered rate, bounded stream reads, and one leased catch-up consumer per tenant whose
// commit-to-checkpoint delivery latency is recorded for every accepted event.
internal sealed class EventsWorkload : CapacityWorkload
{
    private const string ConsumerId = "capacity-consumer";
    private readonly BlueTuskDataSource _source;
    private readonly PostgreSqlEventsOptions _options;
    private readonly PostgreSqlEventStore _store;
    private readonly StreamState[] _streams;
    private readonly int _tenants;
    private readonly int _streamsPerTenant;
    private readonly int _eventsPerAppend;
    private readonly int _payloadBytes;
    private readonly int _readMaximumEvents;
    private readonly int _replayMaximumEvents;
    private readonly int _readWorkers;
    private readonly TimeSpan _leaseDuration;
    private readonly ConcurrentDictionary<Guid, Committed> _committed = new();
    private readonly ConcurrentDictionary<Guid, long> _delivered = new();
    private CapacityRecorder? _recorder;
    private long _acceptedEvents;
    private long _appendTransactions;
    private long _readEvents;

    private sealed class StreamState(int index, EventStreamKey key)
    {
        public int Index { get; } = index;
        public EventStreamKey Key { get; } = key;
        public long Head;
        public long Delivered;
        public EventReplayLease? Lease;
        public long LeaseRenewedTimestamp;
        public EventWrite[]? LastBatch;
    }

    private readonly record struct Committed(int Stream, long Sequence, long CommittedMicroseconds);

    private EventsWorkload(CapacityContext context, BlueTuskDataSource source)
    {
        _source = source;
        _tenants = context.IntParameter("tenants");
        _streamsPerTenant = context.IntParameter("streamsPerTenant");
        _eventsPerAppend = context.IntParameter("eventsPerAppend");
        _payloadBytes = context.IntParameter("payloadBytes");
        _readMaximumEvents = context.IntParameter("readMaximumEvents");
        _replayMaximumEvents = context.IntParameter("replayMaximumEvents");
        _leaseDuration = TimeSpan.FromSeconds(context.IntParameter("replayLeaseSeconds"));
        _readWorkers = context.Schedule("read").Workers;
        CapacityHost.Check(context.Schedule("append").Workers == _tenants && context.Schedule("replay").Workers == _tenants,
            "Events appenders and consumers must own exactly one tenant each.");
        _options = new PostgreSqlEventsOptions { Schema = "events_capacity_" + Guid.NewGuid().ToString("N")[..16] };
        _store = new PostgreSqlEventStore(source, _options);
        _streams = [.. Enumerable.Range(0, _tenants * _streamsPerTenant).Select(index => new StreamState(index,
            new EventStreamKey($"tenant-{index / _streamsPerTenant:D2}", $"stream-{index % _streamsPerTenant:D2}")))];
    }

    public static async Task<CapacityWorkload> CreateAsync(CapacityContext context, CancellationToken cancellationToken)
    {
        var settings = new BlueTuskConnectionStringBuilder(context.ConnectionString)
        {
            MaximumPoolSize = context.IntParameter("maximumPoolSize"),
            ApplicationName = "bluetusk-events-capacity",
        };
        var source = BlueTuskDataSource.Create(settings.ConnectionString);
        var workload = new EventsWorkload(context, source);
        try
        {
            await workload._store.InitializeAsync(cancellationToken);
            return workload;
        }
        catch
        {
            await workload.DisposeAsync();
            throw;
        }
    }

    public override IReadOnlyList<string> OwnedSchemas => [_options.Schema];

    public override IReadOnlyList<ScheduledOperation> Operations =>
    [
        new("append", AppendAsync),
        new("read", ReadAsync),
        new("replay", ReplayAsync),
    ];

    public override Task RunBackgroundAsync(CapacityRecorder recorder, CancellationToken offerEnded, CancellationToken abort)
    {
        _recorder = recorder;
        return Task.CompletedTask;
    }

    private CapacityRecorder Recorder => _recorder ?? throw new InvalidOperationException("The recorder is not attached.");

    private EventWrite[] Batch(int stream, long slot, int count)
    {
        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        var occurred = new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
        var writes = new EventWrite[count];
        var payload = new byte[_payloadBytes];
        for (var item = 0; item < count; item++)
        {
#pragma warning disable CA5394 // Seeded high-entropy payloads are fixture content, not security material.
            new Random(unchecked(17 + stream * 1_000_003 + (int)slot * 7_919 + item * 31)).NextBytes(payload);
#pragma warning restore CA5394
            writes[item] = new EventWrite(Guid.CreateVersion7(), "capacity.order-placed", 1, occurred, payload);
        }

        return writes;
    }

    private async ValueTask<OperationOutcome> AppendAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var state = _streams[worker * _streamsPerTenant + (int)(slot % _streamsPerTenant)];
        var writes = Batch(state.Index, slot, _eventsPerAppend);
        var head = Volatile.Read(ref state.Head);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var receipts = await _store.AppendAsync(connection, transaction, state.Key, writes, cancellationToken);
            CapacityHost.Check(receipts.Count == writes.Length &&
                receipts.Select((receipt, item) => receipt.EventId == writes[item].EventId && !receipt.WasAlreadyStored &&
                    receipt.Sequence == head + item + 1).All(static ok => ok),
                "An Events append did not assign exactly the next gap-free sequences.");
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception error) when (error is not CapacityInvariantException)
        {
            // The commit outcome is unknown: resynchronize the head from the store before the
            // next slot so a later correct append is not misreported as a sequence gap.
            Volatile.Write(ref state.Head, await ReadHeadAsync(state));
            throw;
        }

        var committed = Recorder.ElapsedMicroseconds;
        for (var item = 0; item < writes.Length; item++)
        {
            CapacityHost.Check(_committed.TryAdd(writes[item].EventId, new Committed(state.Index, head + item + 1, committed)),
                "An Events identifier was committed twice.");
        }

        Volatile.Write(ref state.Head, head + writes.Length);
        if (state.Index == 0) { state.LastBatch = writes; }
        Interlocked.Add(ref _acceptedEvents, writes.Length);
        Interlocked.Increment(ref _appendTransactions);
        return OperationOutcome.Accepted;
    }

    private async ValueTask<OperationOutcome> ReadAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var state = _streams[(int)((slot * _readWorkers + worker) % _streams.Length)];
        var head = Volatile.Read(ref state.Head);
        var after = Math.Max(0, head - _readMaximumEvents);
        var events = await _store.ReadAsync(state.Key, after, _readMaximumEvents, _options.MaximumEventBytes, cancellationToken);
        CapacityHost.Check(events.Count >= head - after && events.Count <= _readMaximumEvents,
            "An Events read returned fewer committed events than the stream head guarantees.");
        for (var item = 0; item < events.Count; item++)
        {
            var value = events[item];
            CapacityHost.Check(value.Stream == state.Key && value.Sequence == after + item + 1 &&
                value.Payload.Length == _payloadBytes, "An Events read returned a foreign, out-of-order or truncated event.");
        }

        Interlocked.Add(ref _readEvents, events.Count);
        return OperationOutcome.Accepted;
    }

    private async ValueTask<OperationOutcome> ReplayAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var state = _streams[worker * _streamsPerTenant + (int)(slot % _streamsPerTenant)];
        await ReplayOnceAsync(state, cancellationToken);
        return OperationOutcome.Accepted;
    }

    private async Task<EventReplayResult> ReplayOnceAsync(StreamState state, CancellationToken cancellationToken)
    {
        var lease = await EnsureLeaseAsync(state, cancellationToken);
        var tentative = state.Delivered;
        var handled = new List<Guid>(_replayMaximumEvents);
        EventReplayResult result;
        try
        {
            result = await _store.ReplayAsync(lease, (value, _, _, _) =>
            {
                CapacityHost.Check(value.Stream == state.Key && value.Sequence == tentative + 1 &&
                    value.Payload.Length == _payloadBytes, "Events catch-up delivered a gap, duplicate or foreign event.");
                tentative++;
                handled.Add(value.EventId);
                return ValueTask.CompletedTask;
            }, _replayMaximumEvents, _options.MaximumAppendBytes, cancellationToken);
        }
        catch (EventReplayFencedException)
        {
            state.Lease = null;
            throw;
        }

        CapacityHost.Check(result.HandledCount == handled.Count && result.Checkpoint == tentative,
            "Events catch-up checkpoint differs from the handled batch.");
        var delivered = Recorder.ElapsedMicroseconds;
        foreach (var eventId in handled)
        {
            CapacityHost.Check(_delivered.TryAdd(eventId, delivered), "Events catch-up delivered an event twice.");
        }

        state.Delivered = tentative;
        return result;
    }

    private async Task<EventReplayLease> EnsureLeaseAsync(StreamState state, CancellationToken cancellationToken)
    {
        if (state.Lease is { } current)
        {
            if (Stopwatch.GetElapsedTime(state.LeaseRenewedTimestamp) < _leaseDuration / 3) { return current; }
            if (await _store.RenewReplayAsync(current, _leaseDuration, cancellationToken))
            {
                state.LeaseRenewedTimestamp = Stopwatch.GetTimestamp();
                return current;
            }

            state.Lease = null;
            throw new InvalidOperationException("The Events replay lease could not be renewed.");
        }

        var lease = await _store.AcquireReplayAsync(ConsumerId, state.Key, $"capacity-owner-{state.Index:D3}", _leaseDuration, cancellationToken)
            ?? throw new InvalidOperationException("Another owner holds the Events replay lease.");
        state.Lease = lease;
        state.LeaseRenewedTimestamp = Stopwatch.GetTimestamp();
        return lease;
    }

    public override async Task DrainAndVerifyAsync(CapacityRecorder recorder, CancellationToken cancellationToken)
    {
        // Catch-up drain: every consumer reaches its committed stream head.
        var drain = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, _tenants).Select(tenant => Task.Run(async () =>
        {
            for (var stream = 0; stream < _streamsPerTenant; stream++)
            {
                var state = _streams[tenant * _streamsPerTenant + stream];
                while (state.Delivered < Volatile.Read(ref state.Head))
                {
                    CapacityHost.Check(drain.Elapsed < TimeSpan.FromMinutes(10), "Events catch-up did not drain within ten minutes.");
                    _ = await ReplayOnceAsync(state, cancellationToken);
                }
            }
        }, cancellationToken)));
        recorder.Metric("replayDrainSeconds", drain.Elapsed.TotalSeconds);

        var missing = 0L;
        foreach (var (eventId, committed) in _committed)
        {
            if (!_delivered.TryGetValue(eventId, out var delivered)) { missing++; continue; }
            recorder.RecordSeries("delivery", committed.Stream, committed.Sequence, committed.CommittedMicroseconds,
                Math.Max(0, delivered - committed.CommittedMicroseconds));
        }

        var accepted = Interlocked.Read(ref _acceptedEvents);
        recorder.Counter("acceptedEvents", accepted);
        recorder.Counter("appendTransactions", Interlocked.Read(ref _appendTransactions));
        recorder.Counter("deliveredEvents", _delivered.Count);
        recorder.Counter("readEvents", Interlocked.Read(ref _readEvents));
        recorder.Counter("streams", _streams.Length);
        recorder.Invariant("every-commit-delivered-once", missing == 0 && _delivered.Count == _committed.Count &&
            _committed.Count == accepted, $"committed={_committed.Count}, delivered={_delivered.Count}, missing={missing}");

        var heads = await StreamHeadsAsync();
        recorder.Invariant("stream-heads-exact", heads.Count == _streams.Length &&
            _streams.All(state => heads.TryGetValue(state.Key, out var head) && head == Volatile.Read(ref state.Head)) &&
            heads.Values.Sum() == accepted, $"streams={heads.Count}, events={heads.Values.Sum()}");
        var outbox = await ScalarAsync($"SELECT count(*) FROM \"{_options.Schema}\".outbox");
        var identities = await ScalarAsync($"SELECT count(*) FROM \"{_options.Schema}\".event_identities");
        recorder.Invariant("outbox-and-identities-exact", outbox == accepted && identities == accepted,
            $"outbox={outbox}, identities={identities}");
        var inbox = await ScalarAsync($"SELECT count(*) FROM \"{_options.Schema}\".inbox WHERE consumer_id = '{ConsumerId}'");
        var checkpoints = await ScalarAsync($"""
            SELECT count(*) FROM "{_options.Schema}".replay r JOIN "{_options.Schema}".streams s
              ON s.tenant_id = r.tenant_id AND s.stream_id = r.stream_id
            WHERE r.consumer_id = '{ConsumerId}' AND r.checkpoint = s.last_sequence
            """);
        recorder.Invariant("inbox-and-checkpoints-exact", inbox == accepted && checkpoints == _streams.Length,
            $"inbox={inbox}, exactCheckpoints={checkpoints}");

        // Idempotent retry: the identical last batch must be recognized, not appended again.
        var probe = _streams[0];
        var batch = probe.LastBatch ?? throw new CapacityInvariantException("The probe stream accepted no append.");
        var probeHead = Volatile.Read(ref probe.Head);
        IReadOnlyList<EventAppendReceipt> replayed;
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            replayed = await _store.AppendAsync(connection, transaction, probe.Key, batch, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        recorder.Invariant("idempotent-retry", replayed.Count == batch.Length && replayed.All(static item => item.WasAlreadyStored) &&
            replayed.Select(static item => item.Sequence).SequenceEqual(Enumerable.Range(1, batch.Length).Select(item => probeHead - batch.Length + item)) &&
            await ReadHeadAsync(probe) == probeHead, $"receipts={replayed.Count}");

        // A rolled-back append must not consume a sequence.
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            _ = await _store.AppendAsync(connection, transaction, probe.Key, Batch(0, -1, 1), cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
        }

        IReadOnlyList<EventAppendReceipt> next;
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            next = await _store.AppendAsync(connection, transaction, probe.Key, Batch(0, -2, 1), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        recorder.Invariant("rollback-gap-free", next.Count == 1 && next[0].Sequence == probeHead + 1 && !next[0].WasAlreadyStored,
            string.Create(CultureInfo.InvariantCulture, $"next={next[0].Sequence}, head={probeHead}"));
    }

    private async Task<long> ReadHeadAsync(StreamState state) =>
        await ScalarAsync($"SELECT coalesce((SELECT last_sequence FROM \"{_options.Schema}\".streams WHERE tenant_id = '{state.Key.TenantId}' AND stream_id = '{state.Key.StreamId}'), 0)");

    private async Task<Dictionary<EventStreamKey, long>> StreamHeadsAsync()
    {
        var heads = new Dictionary<EventStreamKey, long>();
        await using var command = _source.CreateCommand($"SELECT tenant_id, stream_id, last_sequence FROM \"{_options.Schema}\".streams");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            heads.Add(new EventStreamKey(reader.GetString(0), reader.GetString(1)), reader.GetInt64(2));
        }

        return heads;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var command = _source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = _source.CreateCommand($"DROP SCHEMA IF EXISTS \"{_options.Schema}\" CASCADE");
            _ = await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await _source.DisposeAsync();
        }
    }
}

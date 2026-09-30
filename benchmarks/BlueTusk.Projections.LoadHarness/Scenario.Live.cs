using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BlueTusk.Live;
using BlueTusk.Projections.Live;

namespace BlueTusk.Projections.LoadHarness;

internal sealed partial class Scenario
{
    private static JsonTypeInfo<LiveResultEvent<ProjectionLiveRow<OrderView>, string>> LiveMetadata =>
        (JsonTypeInfo<LiveResultEvent<ProjectionLiveRow<OrderView>, string>>)ReportJson.Default.GetTypeInfo(typeof(LiveResultEvent<ProjectionLiveRow<OrderView>, string>))!;

    private async Task StartLiveAsync(CancellationToken token)
    {
        var replay = new PostgreSqlProjectionLiveReplayStore(_routing, new()
        {
            Schema = _schema,
            MaximumReadEvents = 2049,
            MaximumAppendEvents = 2048,
            RetentionWindow = TimeSpan.FromMinutes(30),
            PruneBatchRows = 37
        });
        await replay.InitializeAsync(token);
        for (var index = 0; index < _configuration.Tenants; index++)
        {
            var tenant = Tenant(index);
            var query = new ProjectionLiveQuery<OrderView>(_store, "orders", tenant, new("load:" + tenant, "v1"),
                "load-orders", "owned-load", "v1", ReportJson.Default.OrderView,
                new() { MaximumDocuments = Orders(index), MaximumPayloadBytes = 1_048_576 });
            await using var session = query.CreateSession();
            var publisher = await replay.AcquireAsync(session.Identity, "load-live", _physicalProfile && !_recovered ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(2), token)
                ?? throw new InvalidOperationException("Live publisher lease unavailable.");
            var live = new ProjectionLiveSubscription<OrderView>(query, publisher, LiveMetadata,
                subscriptionOptions: new() { MaximumSubscribers = 2, SubscriberBufferCapacity = 2048, MaximumReplayEventsPerConnect = 2048 });
            _live.Add(live); await live.StartAsync(token);
            Interlocked.Exchange(ref _lastLiveRefresh[index], Stopwatch.GetTimestamp());
            var connected = await live.ConnectAsync(0, token);
            Program.Check(connected.Status == LiveSubscriptionConnectStatus.Connected, "authorized Live connect");
            var client = connected.Connection!; _clients.Add(client);
            long? previous = null;
            foreach (var entry in client.Replay)
            {
                Program.Check(previous is null || entry.Sequence == previous + 1, "contiguous authorized replay frames");
                ValidateFrame(entry, tenant); Interlocked.Increment(ref _replayedFrames); previous = entry.Sequence;
            }
            _clientReaders.Add(ReadClientAsync(client, tenant, previous ?? live.Status.PersistedSequence, _readers.Token));
        }
    }

    private async Task ReadClientAsync(LiveSubscriptionConnection client, string tenant, long previous, CancellationToken token)
    {
        try
        {
            await foreach (var message in client.ReadAllAsync(token))
            {
                Program.Check(message.Kind == LiveSubscriberMessageKind.Event && message.Event is not null, "bounded fast subscriber receives persisted frames");
                Program.Check(message.Event!.Sequence == previous + 1, "subscriber receives contiguous persisted frame sequences without duplicates");
                ValidateFrame(message.Event!, tenant);
                previous = message.Event!.Sequence;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<long> DrainSubscriberFramesAsync(CancellationToken token, bool requireConnected = true)
    {
        // Call only after all publisher refresh tasks have stopped. Replay entries are consumed
        // synchronously on connect; each counted fan-out delivery must also reach the decoder.
        var expected = Interlocked.Read(ref _replayedFrames) + _retiredFanOutFrames;
        foreach (var subscription in _live)
        {
            var status = subscription.Status;
            Program.Check(status.SubscriberCount <= 1 && (!requireConnected || status.SubscriberCount == 1)
                && status.SlowClientDisconnects == 0, "bounded subscriber without an unobserved slow-client gap");
            expected += status.FanOutDeliveries;
        }
        while (Interlocked.Read(ref _liveFrames) < expected)
        {
            foreach (var reader in _clientReaders)
            {
                if (reader.IsCompleted) { await reader; Program.Check(false, "subscriber ended before complete frame validation"); }
            }
            await Task.Delay(20, token);
        }
        foreach (var reader in _clientReaders) { if (reader.IsFaulted) { await reader; } }
        Program.Check(Interlocked.Read(ref _liveFrames) == expected, "all counted replay and fan-out frames decoded and integrity checked exactly once");
        return expected;
    }

    private void ValidateFrame(LiveReplayEvent entry, string tenant)
    {
        Program.Check(entry.Payload.Length <= 1_048_576 && LiveReplayJsonSerializer.VerifyIntegrity(entry), "bounded Live frame integrity");
        using var document = JsonDocument.Parse(entry.Payload, new() { MaxDepth = 16 });
        void Validate(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("Tenant")) { Program.Check(property.Value.GetString() == tenant, "no cross-tenant Live delivery"); }
                    else { Validate(property.Value); }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array) { foreach (var child in element.EnumerateArray()) { Validate(child); } }
        }
        Validate(document.RootElement); Interlocked.Increment(ref _liveFrames);
    }

    private async Task RefreshLiveAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            do
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, _live.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (index, cancellation) =>
                {
                    // An idle authorized subscriber still owns a durable writer lease. Refresh its
                    // bounded metadata at least every 30 seconds rather than letting cold tenants
                    // lose ownership merely because overload admission rejected their updates.
                    if (_livePending[index].IsEmpty && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastLiveRefresh[index])) < TimeSpan.FromSeconds(30)) { return; }
                    var started = Stopwatch.GetTimestamp();
                    await _live[index].RefreshAsync(cancellation);
                    var covered = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref _lastLiveRefresh[index], covered);
                    while (_livePending[index].TryPeek(out var row) && Volatile.Read(ref row.Projected) <= started)
                    {
                        Program.Check(_livePending[index].TryDequeue(out row), "one Live coverage consumer per tenant");
                        Interlocked.CompareExchange(ref row!.Live, covered, 0);
                    }
                });
                foreach (var reader in _clientReaders) { if (reader.IsFaulted) { await reader; } }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task VerifyAsync(CancellationToken token)
    {
        Program.Check(_operations.Count <= _configuration.MaximumOperations && _peakQueued <= _configuration.QueueCapacity + _configuration.Writers,
            "operation/queue admission bounds");
        Program.Check(_operations.Values.All(static row => row.Committed != 0 && row.Projected != 0 && row.Inbox != 0 && row.Live != 0), "all accepted operations reached every durable boundary");
        await using (var connection = await _source.OpenConnectionAsync(token))
        await using (var command = Sql.Command(connection, null, $"""
            SELECT (SELECT count(*) FROM "{_schema}".operations),(SELECT count(*) FROM "{_eventsSchema}".outbox),
                (SELECT count(*) FROM "{_eventsSchema}".effects),(SELECT count(*) FROM "{_eventsSchema}".inbox WHERE consumer_id='load-wal'),
                (SELECT count(*) FROM "{_schema}".operations o FULL JOIN "{_eventsSchema}".effects e ON e.id=o.id WHERE o.id IS NULL OR e.id IS NULL OR o.tenant<>e.tenant)
            """))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            Program.Check(await reader.ReadAsync(token), "exact durable counters");
            for (var index = 0; index < 4; index++) { Program.Check(reader.GetInt64(index) == _operations.Count, "exact operation/outbox/inbox/effect cardinality"); }
            Program.Check(reader.GetInt64(4) == 0 && _inboxEffects == _operations.Count, "no missing, extra, duplicate or cross-tenant effects");
        }
        for (var index = 0; index < _configuration.Tenants; index++)
        {
            var tenant = Tenant(index);
            var expected = new Dictionary<string, OrderView>(StringComparer.Ordinal);
            await using (var connection = await _source.OpenConnectionAsync(token))
            await using (var command = Sql.Command(connection, null, $"SELECT o.id,o.amount,c.name FROM \"{_schema}\".orders o JOIN \"{_schema}\".customers c ON c.tenant=o.tenant AND c.id=o.customer WHERE o.tenant=@tenant ORDER BY o.id COLLATE \"C\"", ("tenant", tenant)))
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    Program.Check(expected.Count < Orders(index), "bounded source verification rows");
                    expected.Add(reader.GetString(0), new(reader.GetString(0), tenant, reader.GetString(2), decimal.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture), _lease!.Identity.Version, _padding));
                }
            }
            ProjectionPageCursor? after = null;
            var actual = new Dictionary<string, OrderView>(StringComparer.Ordinal);
            do
            {
                var page = await _store.ReadActivePageAsync("orders", tenant, 64, 1_048_576, after, cancellationToken: token);
                foreach (var document in page.Documents)
                {
                    Program.Check(actual.Count < Orders(index) && document.TenantId == tenant, "bounded tenant projection pagination");
                    var value = JsonSerializer.Deserialize(document.Payload.Span, ReportJson.Default.OrderView)!;
                    Program.Check(expected.TryGetValue(document.Key, out var wanted) && wanted == value, "exact final joined document equals authoritative SQL");
                    actual.Add(document.Key, value);
                }
                after = page.ContinueAfter;
            } while (after is not null);
            Program.Check(actual.Count == expected.Count && actual.Count == Orders(index), "exact joined read-model cardinality");
            var expectedTotal = _operations.Values.LongCount(row => row.Tenant == index && !row.CustomerChange);
            Program.Check((await _store.ReadActiveAggregateAsync("orders", tenant, "all", "total", token)).Value == expectedTotal && actual.Values.Sum(static row => row.Amount) == expectedTotal, "exact aggregate and business ledger count");
            await using var counts = await _source.OpenConnectionAsync(token);
            await using var contiguous = Sql.Command(counts, null, $"SELECT count(*),coalesce(min(sequence),0),coalesce(max(sequence),0),coalesce(sum(sequence),0) FROM \"{_eventsSchema}\".effects WHERE tenant=@tenant", ("tenant", tenant));
            await using var sequences = await contiguous.ExecuteReaderAsync(token);
            Program.Check(await sequences.ReadAsync(token), "tenant contiguous sequence row");
            var count = _operations.Values.LongCount(row => row.Tenant == index);
            Program.Check(sequences.GetInt64(0) == count && sequences.GetInt64(1) == (count == 0 ? 0 : 1) && sequences.GetInt64(2) == count && sequences.GetDecimal(3) == (decimal)count * (count + 1) / 2, "tenant sequence has no gaps/duplicates");
        }
        var state = await _store.ReadStateAsync(_lease!.Identity, token);
        Program.Check(state.Phase == ProjectionBuildPhase.CatchingUp && state.Checkpoint >= _snapshot!.Epoch.ConsistentPosition, "snapshot/WAL checkpoint coverage");
        // Reconnect after final drain: all retained frames are integrity/tenant checked, and a new
        // connection uses the persisted head without a transient owner-side sequence reservation.
        for (var index = 0; index < _live.Count; index++)
        {
            var connected = await _live[index].ConnectAsync(_live[index].Status.PersistedSequence, token);
            Program.Check(connected.Status == LiveSubscriptionConnectStatus.Connected, "bounded reconnect at durable Live head");
            foreach (var entry in connected.Connection!.Replay) { ValidateFrame(entry, Tenant(index)); }
            await connected.Connection.DisposeAsync();
        }
    }
}

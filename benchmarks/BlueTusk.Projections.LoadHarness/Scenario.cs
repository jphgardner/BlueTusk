using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using BlueTusk.Data;
using BlueTusk.Events;
using BlueTusk.Events.Streams;
using BlueTusk.Live;
using BlueTusk.Projections.Live;
using BlueTusk.Replication;
using BlueTusk.Streams;

namespace BlueTusk.Projections.LoadHarness;

internal sealed partial class Scenario : IAsyncDisposable
{
    private sealed class Operation(Guid id, int tenant, int order, bool customerChange, long offered)
    {
        internal Guid Id { get; } = id;
        internal int Tenant { get; } = tenant;
        internal int Order { get; } = order;
        internal bool CustomerChange { get; } = customerChange;
        internal long Offered { get; } = offered;
        internal long Committed, Projected, Inbox, Live;
    }
    private readonly LoadCase _configuration;
    private BlueTuskDataSource _source;
    private readonly RoutedDataSource _routing;
    private BlueTuskDataSource? _originalSource;
    // The serial CDC delivery path (projection apply, Events inbox, lease rotation) owns a separate small
    // pool, as a production projection worker would: application writers saturating their own pool
    // during a storage stall must not queue the single ordered consumer behind them.
    private BlueTuskDataSource _deliverySource;
    private readonly RoutedDataSource _deliveryRouting;
    private BlueTuskDataSource? _originalDeliverySource;
    private readonly PostgreSqlProjectionStore _deliveryStore;
    private readonly string _schema = "proj_load_" + Guid.NewGuid().ToString("N")[..16];
    private readonly string _eventsSchema;
    private readonly string _publication;
    private readonly string _slot;
    private readonly PostgreSqlProjectionStore _store;
    private readonly PostgreSqlEventStore _events;
    private readonly ConcurrentDictionary<Guid, Operation> _operations = new();
    private readonly long[] _offered, _rejected, _committedByTenant, _deliveredByTenant;
    private readonly List<ServiceWindow> _serviceWindows = [];
    private readonly ConcurrentQueue<Operation>[] _livePending;
    private readonly long[] _lastLiveRefresh;
    private readonly List<ProjectionLiveSubscription<OrderView>> _live = [];
    private readonly List<LiveSubscriptionConnection> _clients = [];
    private readonly List<Task> _clientReaders = [];
    private CancellationTokenSource _readers = new();
    private bool _physicalProfile;
    private bool _recovered;
    private readonly string _padding;
    private IConsistentSnapshotAttempt? _snapshot;
    private IAsyncEnumerator<ChangeTransactionDelivery>? _walReader;
    private CancellationTokenSource? _walCancellation;
    private ChangeSourceIdentity? _identity;
    private ProjectionLease? _lease;
    private OrdersProjection? _definition;
    private PostgreSqlEventDeliveryProcessor? _processor;
    private long _walTransactions, _inboxEffects, _liveFrames, _duplicateRetries, _leaseRecoveries;
    private long _standbyFeedbackUpdates;
    private long _replayedFrames, _retiredFanOutFrames;
    private long _accepted;
    private int _queued, _peakQueued;
    private long _queueBytes, _peakQueueBytes;
    private readonly int _eventLimit;
    private readonly string _connection;
    private readonly string? _diagnosticsReport;
    private DeliveryTrace? _trace;
    private ServerSampler? _sampler;

    private Scenario(string connection, LoadCase configuration, string? diagnosticsReport)
    {
        _configuration = configuration;
        _connection = connection;
        _diagnosticsReport = diagnosticsReport;
        _eventsSchema = _schema + "_events"; _publication = _schema + "_pub"; _slot = _schema + "_slot";
        var settings = new BlueTuskConnectionStringBuilder(connection)
        { MaximumPoolSize = configuration.PoolSize, ApplicationName = "BlueTuskProjectionsLoadHarness" };
        _source = BlueTuskDataSource.Create(settings.ConnectionString);
        _routing = new(_source);
        _deliverySource = CreateDeliverySource(connection, configuration);
        _deliveryRouting = new(_deliverySource);
        PostgreSqlProjectionsOptions StoreOptions() => new()
        {
            Schema = _schema,
            MaximumSnapshotBatchRows = 64,
            MaximumInvalidationsPerTransaction = 4096,
            MaximumWriteOperationsPerTransaction = 16_384,
            MaximumResetBatchRows = 37
        };
        _store = new(_routing, StoreOptions());
        _deliveryStore = new(_deliveryRouting, StoreOptions());
        _eventLimit = configuration.PayloadBytes + 1024;
        _events = new(_routing, new() { Schema = _eventsSchema, MaximumEventBytes = _eventLimit, MaximumAppendBytes = 1_048_576 });
        _offered = new long[configuration.Tenants]; _rejected = new long[configuration.Tenants];
        _committedByTenant = new long[configuration.Tenants]; _deliveredByTenant = new long[configuration.Tenants];
        _livePending = Enumerable.Range(0, configuration.Tenants).Select(static _ => new ConcurrentQueue<Operation>()).ToArray();
        _lastLiveRefresh = new long[configuration.Tenants];
        // Deterministic high-entropy ASCII padding exercises byte bounds and physical WAL rather
        // than benchmarking PostgreSQL's compression of one repeated character.
        var padding = new char[Math.Max(0, configuration.PayloadBytes - 180)];
        var random = new Random(84721);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        for (var index = 0; index < padding.Length; index++) { padding[index] = alphabet[random.Next(alphabet.Length)]; }
        _padding = new string(padding);
    }

    internal static async Task<ScenarioReport> RunAsync(string connection, LoadCase configuration, bool promotion, string? diagnosticsReport = null)
    {
        await using var scenario = new Scenario(connection, configuration, diagnosticsReport);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(configuration.Seconds + configuration.DrainSeconds + 1200));
        return await scenario.RunCoreAsync(promotion, lifetime.Token);
    }

    private static BlueTuskDataSource CreateDeliverySource(string connection, LoadCase configuration) =>
        BlueTuskDataSource.Create(new BlueTuskConnectionStringBuilder(connection)
        { MaximumPoolSize = configuration.DeliveryPoolSize, ApplicationName = "BlueTuskProjectionsLoadHarness" }.ConnectionString);

    private static string Tenant(int index) => "tenant_" + index.ToString("D3", CultureInfo.InvariantCulture);
    private static string Order(int index) => index.ToString("D4", CultureInfo.InvariantCulture);
    private int Orders(int tenant) => tenant == 0 ? _configuration.Fanout : 1;

    private async Task<ScenarioReport> RunCoreAsync(bool promotion, CancellationToken token)
    {
        _physicalProfile = promotion;
        _walCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var setup = Stopwatch.GetTimestamp();
        await InitializeAsync(token);
        await BuildInitialSnapshotAsync(token);
        if (promotion) { await StartLiveAsync(token); }
        if (_diagnosticsReport is not null)
        {
            // Diagnostic-only JSONL outputs; existing report fields and verifier inputs are unchanged.
            _trace = await DeliveryTrace.StartAsync(DeliveryTrace.PathFor(_diagnosticsReport, _configuration.Name, "delivery"), _source, _configuration, token);
            _sampler = ServerSampler.StartIfEnabled(_connection, DeliveryTrace.PathFor(_diagnosticsReport, _configuration.Name, "server"), _trace);
        }
        var before = await DatabaseProbe.StorageAsync(_source, _schema, _eventsSchema, token);
        var setupSeconds = Stopwatch.GetElapsedTime(setup).TotalSeconds;
        var measured = Stopwatch.GetTimestamp();
        // Backlog consists of acknowledged real SQL+outbox commits after the exported snapshot,
        // while WAL delivery is intentionally stopped. It is included in latency/storage evidence.
        await SeedBacklogAsync(token);
        var pendingBefore = _operations.Count;
        RecoveryObservation? recovery = null;
        if (promotion) { recovery = await PromoteUnderBacklogAsync(pendingBefore, token); }
        else { await StartLiveAsync(token); }
        var runtimeStarted = Stopwatch.GetTimestamp();
        await using var probe = new DatabaseProbe(_source, _schema, _eventsSchema, _identity!.SlotName);
        using var pipeline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var wal = ConsumeWalAsync(pipeline.Token);
        var lives = RefreshLiveAsync(pipeline.Token);
        var service = ObserveServiceAsync(probe, pipeline.Token);
        var monitor = MonitorAsync([wal, lives, service, probe.Completion], pipeline);
        var queue = Channel.CreateBounded<Operation>(new BoundedChannelOptions(_configuration.QueueCapacity)
        { SingleWriter = true, SingleReader = _configuration.Writers == 1, FullMode = BoundedChannelFullMode.Wait });
        var writers = Enumerable.Range(0, _configuration.Writers).Select(_ => ProduceAsync(queue.Reader, pipeline.Token)).ToArray();
        var offeredStarted = Stopwatch.GetTimestamp();
        long offeredEnded = 0;
        TimeSpan offeredElapsed;
        long drain;
        // Subscriber buffers drain after publishers stop, so this deadline must survive controlled
        // pipeline cancellation while still respecting the scenario's lifetime and drain bound.
        using var drainDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            await OfferAsync(queue.Writer, offeredStarted, pipeline.Token);
            queue.Writer.Complete();
            offeredEnded = Stopwatch.GetTimestamp();
            offeredElapsed = Stopwatch.GetElapsedTime(offeredStarted, offeredEnded);
            drain = Stopwatch.GetTimestamp();
            drainDeadline.CancelAfter(TimeSpan.FromSeconds(_configuration.DrainSeconds));
            await Task.WhenAll(writers).WaitAsync(drainDeadline.Token);
            while (_operations.Values.Any(static operation => Volatile.Read(ref operation.Inbox) == 0 || Volatile.Read(ref operation.Projected) == 0 || Volatile.Read(ref operation.Live) == 0))
            {
                pipeline.Token.ThrowIfCancellationRequested();
                if (wal.IsCompleted) { await wal; Program.Check(false, "WAL consumer ended before complete coverage"); }
                if (lives.IsCompleted) { await lives; Program.Check(false, "Live worker ended before complete coverage"); }
                await Task.Delay(20, drainDeadline.Token);
            }
        }
        finally
        {
            queue.Writer.TryComplete();
            await pipeline.CancelAsync();
            await _walCancellation.CancelAsync();
            try { await Task.WhenAll([wal, lives, service, .. writers]); }
            catch (OperationCanceledException) when (pipeline.IsCancellationRequested) { }
            await monitor;
        }
        var expectedFrames = await DrainSubscriberFramesAsync(drainDeadline.Token);
        Program.Check(Interlocked.Read(ref _standbyFeedbackUpdates) == Interlocked.Read(ref _walTransactions),
            "each durably settled WAL transaction sends exactly one confirmed-position feedback update");
        var pipelineSeconds = Stopwatch.GetElapsedTime(measured).TotalSeconds;
        var runtimeSeconds = Stopwatch.GetElapsedTime(runtimeStarted).TotalSeconds;
        var drained = Stopwatch.GetElapsedTime(drain).TotalSeconds;
        var runtime = await probe.FinishAsync();
        await CompleteTraceAsync(offeredStarted, offeredEnded, token);
        var verificationStarted = Stopwatch.GetTimestamp();
        await VerifyAsync(token);
        // A new query transaction refreshes statistics snapshots; no global RESET/CHECKPOINT/VACUUM.
        var after = await DatabaseProbe.StorageAsync(_source, _schema, _eventsSchema, token);
        var committed = _operations.Values.Count(static operation => Volatile.Read(ref operation.Committed) != 0);
        var offeredRows = _operations.Values.Where(operation => operation.Offered >= offeredStarted).ToArray();
        long CompletedDuringOffer(Func<Operation, long> read) => offeredRows.LongCount(row =>
        {
            var timestamp = read(row);
            return timestamp != 0 && timestamp <= offeredEnded;
        });
        var offeredDuring = _offered.Sum() - _configuration.Backlog;
        var rejectedDuring = _rejected.Sum();
        Program.Check(offeredDuring == offeredRows.LongLength + rejectedDuring, "offer-window admission is exact");
        var inboxDuring = CompletedDuringOffer(static row => Volatile.Read(ref row.Inbox));
        var offerWindow = new OfferWindowReport(offeredDuring, offeredRows.LongLength, rejectedDuring,
            CompletedDuringOffer(static row => Volatile.Read(ref row.Committed)),
            CompletedDuringOffer(static row => Volatile.Read(ref row.Projected)), inboxDuring,
            CompletedDuringOffer(static row => Volatile.Read(ref row.Live)), offeredRows.LongLength - inboxDuring,
            offeredRows.LongLength / offeredElapsed.TotalSeconds, inboxDuring / offeredElapsed.TotalSeconds);
        var tenants = new List<TenantReport>();
        for (var index = 0; index < _configuration.Tenants; index++)
        {
            var rows = _operations.Values.Where(operation => operation.Tenant == index).ToArray();
            var aggregate = (await _store.ReadActiveAggregateAsync("orders", Tenant(index), "all", "total", token)).Value;
            tenants.Add(new(Tenant(index), _offered[index], _rejected[index], rows.LongLength, rows.LongCount(static row => row.Inbox != 0),
                aggregate, Latency(rows, static row => row.Committed), Latency(rows, static row => row.Projected),
                Latency(rows, static row => row.Inbox), Latency(rows, static row => row.Live)));
        }
        var verificationSeconds = Stopwatch.GetElapsedTime(verificationStarted).TotalSeconds;
        return new(_configuration,
            "Bounded open-loop offers and explicit queue/pending-work admission rejection; SQL operation ledger+business+immutable outbox atomic; one ordered raw Streams CDC reader; joined mirrors/aggregates; transactional WAL inbox; one durable owned Live query per tenant. Fanout applies to tenant_000; cold tenants each have one order. Live latency measures conservative persisted refresh coverage, not network receipt. PayloadBytes targets deterministic high-entropy ASCII padding; serialized envelope overhead is bounded by +1024 bytes. Runtime CPU/allocation/pool maxima cover concurrent pipeline+drain, excluding backlog seeding and physical recovery. Storage/WAL before/after include those phases. Permanent Events identities/outbox/inbox are retained.",
            after.ServerVersion, setupSeconds, offeredElapsed.TotalSeconds, drained, pipelineSeconds, runtimeSeconds, verificationSeconds,
            _offered.Sum(), _rejected.Sum(), committed, _walTransactions, _standbyFeedbackUpdates, _inboxEffects, expectedFrames, _replayedFrames, expectedFrames - _replayedFrames,
            _duplicateRetries, _leaseRecoveries, _peakQueued, _peakQueueBytes, committed / pipelineSeconds,
            Latency(_operations.Values, static row => row.Committed), Latency(_operations.Values, static row => row.Projected),
            Latency(_operations.Values, static row => row.Inbox), Latency(_operations.Values, static row => row.Live), tenants.ToArray(),
            runtime, before, after, recovery, _serviceWindows.ToArray(), offerWindow, true);
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        await _store.InitializeAsync(token); await _events.InitializeAsync(token);
        await Sql.ExecuteAsync(_source, $"""
            CREATE TABLE "{_schema}".customers(id text NOT NULL,tenant text NOT NULL,name text NOT NULL,PRIMARY KEY(tenant,id));
            CREATE TABLE "{_schema}".orders(id text NOT NULL,tenant text NOT NULL,customer text NOT NULL,amount text NOT NULL,payload text NOT NULL,PRIMARY KEY(tenant,id));
            CREATE TABLE "{_schema}".operations(id uuid PRIMARY KEY,tenant text NOT NULL,order_id text NOT NULL,customer_change boolean NOT NULL);
            CREATE TABLE "{_eventsSchema}".effects(id uuid PRIMARY KEY,tenant text NOT NULL,sequence bigint NOT NULL,UNIQUE(tenant,sequence));
            ALTER TABLE "{_schema}".customers REPLICA IDENTITY FULL;
            ALTER TABLE "{_schema}".orders REPLICA IDENTITY FULL;
            CREATE PUBLICATION "{_publication}" FOR TABLE "{_schema}".customers,"{_schema}".orders,"{_eventsSchema}".outbox
            """, token);
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        for (var index = 0; index < _configuration.Tenants; index++)
        {
            await using var command = Sql.Command(connection, transaction, $"""
                INSERT INTO "{_schema}".customers VALUES('customer',@tenant,@tenant);
                INSERT INTO "{_schema}".orders SELECT lpad(n::text,4,'0'),@tenant,'customer','0',@payload FROM generate_series(0,@count-1) n
                """, ("tenant", Tenant(index)), ("count", Orders(index)), ("payload", _padding));
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    private async Task BuildInitialSnapshotAsync(CancellationToken token)
    {
        BlueTuskReplicationSystemIdentity system;
        await using (var identify = await BlueTuskLogicalReplicationConnection.OpenAsync(_source.CreateDedicatedSessionOptions(), token))
        { system = await identify.IdentifySystemAsync(token); }
        _identity = new(system.SystemIdentifier, system.DatabaseName!, _slot, _publication);
        var lineage = await PostgreSqlProjectionLineage.CaptureAsync(_source, _identity, [_publication], token);
        var identity = new ProjectionIdentity("orders", 1, "load-orders-v1", _identity);
        await _store.RegisterWithLineageAsync(identity, lineage, token);
        _lease = await _store.AcquireAsync(identity, "load-owner", TimeSpan.FromHours(2), token) ?? throw new InvalidOperationException("Projection lease unavailable.");
        _definition = new(identity) { DependencyPageSize = 17 };
        var snapshotSource = SnapshotSource(_source, _identity, lineage);
        _snapshot = await snapshotSource.BeginAttemptAsync(null, token);
        var consumer = new StreamsProjectionConsumer(_store, _lease, _definition);
        await PopulateSnapshotAsync(consumer, _snapshot, token);
        await _store.PromoteAsync(_lease, _snapshot.Epoch.ConsistentPosition, null, token);
        _processor = new(_deliveryRouting, _events, new(_eventsSchema, _eventLimit), "load-wal", _identity,
            new() { MaximumEvents = 64, MaximumPayloadBytes = 1_048_576, MaximumSourceChanges = 256 });
    }

    private PostgreSqlConsistentSnapshotSource SnapshotSource(BlueTuskDataSource source, ChangeSourceIdentity identity, ProjectionSourceLineage lineage) =>
        new(source, new()
        {
            Source = identity,
            PublicationNames = [identity.PublicationFingerprint],
            MaximumBatchRows = 64,
            MaximumParallelTables = 1,
            Tables = lineage.Tables.Select(static table => new PostgreSqlSnapshotTable(table, table.Columns.Where(static column => column.IsKey).Select(static column => column.Ordinal))).ToArray(),
            TransactionAssembly = new() { MaxChangesPerTransaction = 256, MaxTransactionBytes = 4_194_304, MaxInMemoryTransactionBytes = 4_194_304 }
        }, observerFactory: connection => new DurableBoundaryFeedbackObserver(connection,
            () => Interlocked.Increment(ref _standbyFeedbackUpdates)));

    private static async Task PopulateSnapshotAsync(StreamsProjectionConsumer consumer, IConsistentSnapshotAttempt snapshot, CancellationToken token)
    {
        await consumer.StartSnapshotAsync(new(snapshot.Epoch, snapshot.Tables.Count), token);
        long rows = 0;
        await foreach (var batch in snapshot.ReadSnapshotAsync(token)) { rows += batch.Rows.Count; await consumer.ConsumeSnapshotBatchAsync(batch, token); }
        await consumer.CompleteSnapshotAsync(new(snapshot.Epoch, rows, snapshot.Tables.Count), token);
    }

    private async Task SeedBacklogAsync(CancellationToken token)
    {
        var batchSize = Math.Min(64, Math.Max(1, 1_048_576 / _eventLimit));
        for (var offset = 0; offset < _configuration.Backlog; offset += batchSize)
        {
            var count = Math.Min(batchSize, _configuration.Backlog - offset);
            var rows = Enumerable.Range(offset, count).Select(index => new Operation(Guid.NewGuid(), index % _configuration.Tenants,
                index % Orders(index % _configuration.Tenants), false, Stopwatch.GetTimestamp())).ToArray();
            foreach (var row in rows) { Program.Check(_operations.TryAdd(row.Id, row), "unique workload operation identity"); Interlocked.Increment(ref _offered[row.Tenant]); }
            _accepted += rows.Length;
            await CommitBatchAsync(rows, token);
        }
    }

    private async Task OfferAsync(ChannelWriter<Operation> writer, long started, CancellationToken token)
    {
        var next = 0L;
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < _configuration.Seconds)
        {
            token.ThrowIfCancellationRequested();
            var due = started + next * Stopwatch.Frequency / _configuration.OfferedPerSecond;
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), due);
            if (remaining.TotalMilliseconds >= 1) { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, 10)), token); continue; }
            // Ten hot offers for every one cold offer, cycling all cold tenants deterministically.
            var tenant = _configuration.Tenants == 1 || next % 11 != 10 ? 0 : 1 + (int)(next / 11 % (_configuration.Tenants - 1));
            Interlocked.Increment(ref _offered[tenant]);
            var operation = new Operation(Guid.NewGuid(), tenant, (int)(next % Orders(tenant)), next % 17 == 0, due);
            if (_accepted >= _configuration.MaximumOperations || _accepted - Interlocked.Read(ref _inboxEffects) >= _configuration.MaximumPendingOperations)
            { Interlocked.Increment(ref _rejected[tenant]); next++; continue; }
            Program.Check(_operations.TryAdd(operation.Id, operation), "unique offered identity");
            var queued = Interlocked.Increment(ref _queued); var bytes = Interlocked.Add(ref _queueBytes, _eventLimit);
            if (!writer.TryWrite(operation))
            { _operations.TryRemove(operation.Id, out _); Interlocked.Decrement(ref _queued); Interlocked.Add(ref _queueBytes, -_eventLimit); Interlocked.Increment(ref _rejected[tenant]); }
            else { _accepted++; _peakQueued = Math.Max(_peakQueued, queued); _peakQueueBytes = Math.Max(_peakQueueBytes, bytes); }
            next++;
        }
    }

    private async Task ProduceAsync(ChannelReader<Operation> queue, CancellationToken token)
    {
        await foreach (var row in queue.ReadAllAsync(token))
        {
            Interlocked.Decrement(ref _queued); Interlocked.Add(ref _queueBytes, -_eventLimit);
            await CommitBatchAsync([row], token);
            if ((row.Id.ToByteArray()[0] & 63) == 0)
            { await CommitBatchAsync([row], token); Interlocked.Increment(ref _duplicateRetries); }
        }
    }

    private async Task CommitBatchAsync(Operation[] rows, CancellationToken token)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        // Consistent lock order avoids cross-stream append/head deadlocks in multi-tenant backlog batches.
        foreach (var row in rows.OrderBy(static row => row.Tenant).ThenBy(static row => row.Order))
        {
            await using var ledger = Sql.Command(connection, transaction, $"INSERT INTO \"{_schema}\".operations VALUES(@id,@tenant,@order,@customer) ON CONFLICT DO NOTHING",
                ("id", row.Id), ("tenant", Tenant(row.Tenant)), ("order", Order(row.Order)), ("customer", row.CustomerChange));
            var isNew = await ledger.ExecuteNonQueryAsync(token) == 1;
            if (isNew)
            {
                await using var business = row.CustomerChange
                    ? Sql.Command(connection, transaction, $"UPDATE \"{_schema}\".customers SET name=@name WHERE tenant=@tenant AND id='customer'", ("name", Tenant(row.Tenant) + ":" + row.Id.ToString("N")), ("tenant", Tenant(row.Tenant)))
                    : Sql.Command(connection, transaction, $"UPDATE \"{_schema}\".orders SET amount=(amount::bigint+1)::text WHERE tenant=@tenant AND id=@order", ("tenant", Tenant(row.Tenant)), ("order", Order(row.Order)));
                Program.Check(await business.ExecuteNonQueryAsync(token) == 1, "one tenant-isolated business effect");
            }
            // Stable event identity/time/content across retry; timestamp is deterministic from identity bytes.
            var payload = JsonSerializer.SerializeToUtf8Bytes(new LoadEvent(row.Id, Tenant(row.Tenant), Order(row.Order), row.CustomerChange, _padding), ReportJson.Default.LoadEvent);
            var value = new EventWrite(row.Id, "load.order", 1, DateTimeOffset.UnixEpoch, payload);
            var receipt = await _events.AppendAsync(connection, transaction, new(Tenant(row.Tenant), "load"), [value], token);
            Program.Check(receipt.Count == 1 && receipt[0].WasAlreadyStored == !isNew, "atomic business/event deduplication");
        }
        await transaction.CommitAsync(token);
        var committed = Stopwatch.GetTimestamp();
        foreach (var row in rows) { if (Interlocked.CompareExchange(ref row.Committed, committed, 0) == 0) { Interlocked.Increment(ref _committedByTenant[row.Tenant]); } }
    }

    private async Task ConsumeWalAsync(CancellationToken token)
    {
        try
        {
            _walReader ??= _snapshot!.CreateChangeStream().ReadTransactionsAsync(_walCancellation!.Token).GetAsyncEnumerator(_walCancellation.Token);
            while (true)
            {
                // Diagnostic phase attribution only: provider checkouts, pool waits, resets, commands and
                // replication receive measurements on this flow are summed per delivery phase.
                var receive = DeliveryTrace.Begin();
                var waitStarted = Stopwatch.GetTimestamp();
                var more = await _walReader.MoveNextAsync();
                DeliveryTrace.End();
                if (!more) { break; }
                var received = Stopwatch.GetTimestamp();
                var pool = _source.GetPoolStatistics();
                var deliveryPool = _deliverySource.GetPoolStatistics();
                var delivery = _walReader.Current;
                await using (delivery)
                {
                    var events = new List<Operation>();
                    var decoder = new EventOutboxChangeDecoder(_eventsSchema, _eventLimit);
                    await foreach (var change in delivery.Transaction.Changes.WithCancellation(token))
                    {
                        if (decoder.TryDecode(change, out var value))
                        {
                            Program.Check(_operations.TryGetValue(value.EventId, out var row), "WAL event belongs to accepted ledger");
                            Program.Check(row!.Tenant.ToString("D3", CultureInfo.InvariantCulture) == value.Stream.TenantId[7..], "WAL event tenant identity");
                            events.Add(row);
                        }
                    }
                    var decoded = Stopwatch.GetTimestamp();
                    var apply = DeliveryTrace.Begin();
                    await _deliveryStore.ApplyAsync(_lease!, _definition!, delivery.Transaction, token);
                    DeliveryTrace.End();
                    var projected = Stopwatch.GetTimestamp();
                    foreach (var row in events) { if (Interlocked.CompareExchange(ref row.Projected, projected, 0) == 0) { _livePending[row.Tenant].Enqueue(row); } }
                    var process = DeliveryTrace.Begin();
                    var result = await _processor!.ProcessAsync(delivery, HandleAsync, token);
                    DeliveryTrace.End();
                    Interlocked.Add(ref _inboxEffects, result.HandledEvents);
                    var inbox = Stopwatch.GetTimestamp();
                    foreach (var row in events) { if (Interlocked.CompareExchange(ref row.Inbox, inbox, 0) == 0) { Interlocked.Increment(ref _deliveredByTenant[row.Tenant]); } }
                    var count = Interlocked.Increment(ref _walTransactions);
                    DeliveryTrace.Phase? lease = null;
                    long leaseStarted = 0, leaseEnded = 0;
                    if (count % 127 == 0)
                    {
                        lease = DeliveryTrace.Begin();
                        leaseStarted = Stopwatch.GetTimestamp();
                        var stale = _lease!; Program.Check(await _deliveryStore.ReleaseAsync(stale, token), "release projection owner");
                        _lease = await _deliveryStore.AcquireAsync(stale.Identity, "recovered-" + count.ToString(CultureInfo.InvariantCulture), TimeSpan.FromHours(2), token)
                            ?? throw new InvalidOperationException("Replacement projection lease unavailable.");
                        Program.Check(!await _deliveryStore.RenewAsync(stale, TimeSpan.FromMinutes(1), token), "stale projection owner rejected");
                        Program.Check(!(await _deliveryStore.ApplyAsync(_lease, _definition!, delivery.Transaction, token)).WasApplied, "durable WAL redelivery checkpoint deduplicates");
                        Interlocked.Increment(ref _leaseRecoveries);
                        DeliveryTrace.End();
                        leaseEnded = Stopwatch.GetTimestamp();
                    }
                    TraceTransaction(count, delivery.Transaction, events, pool, deliveryPool, receive, waitStarted, received, decoded,
                        apply, projected, process, inbox, lease, leaseStarted, leaseEnded);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void TraceTransaction(long count, ChangeTransaction transaction, List<Operation> events, BlueTuskPoolStatistics pool,
        BlueTuskPoolStatistics deliveryPool,
        DeliveryTrace.Phase receive, long waitStarted, long received, long decoded, DeliveryTrace.Phase apply, long projected,
        DeliveryTrace.Phase process, long inbox, DeliveryTrace.Phase? lease, long leaseStarted, long leaseEnded)
    {
        if (_trace is not { } trace) { return; }
        trace.Write(writer =>
        {
            writer.WriteString("k", "tx");
            writer.WriteNumber("n", count);
            writer.WriteNumber("xid", transaction.TransactionId);
            writer.WriteNumber("lsn", transaction.CommitEndPosition.Value);
            writer.WriteNumber("commitUnixMs", Math.Round((transaction.CommitTimestamp - DateTimeOffset.UnixEpoch).TotalMilliseconds, 3));
            writer.WriteNumber("changes", transaction.Changes.Count);
            writer.WriteNumber("events", events.Count);
            writer.WriteNumber("tenant", events.Count == 0 ? -1 : events[0].Tenant);
            writer.WriteBoolean("cust", events.Exists(static row => row.CustomerChange));
            writer.WriteNumber("poolBusy", pool.Busy);
            writer.WriteNumber("poolWaiting", pool.Waiting);
            writer.WriteNumber("deliveryPoolBusy", deliveryPool.Busy);
            writer.WriteNumber("deliveryPoolWaiting", deliveryPool.Waiting);
            writer.WriteNumber("wait0", DeliveryTrace.Round(trace.Milliseconds(waitStarted)));
            writer.WriteNumber("recv", DeliveryTrace.Round(trace.Milliseconds(received)));
            writer.WriteNumber("msgs", receive.Messages);
            writer.WriteNumber("rxLagMs", DeliveryTrace.Round(receive.LastReceiveLagMilliseconds));
            writer.WriteNumber("rxAt", DeliveryTrace.Round(trace.Milliseconds(receive.LastReceiveAt)));
            DeliveryTrace.WritePhase(writer, "rx", receive, waitStarted, received);
            writer.WriteNumber("decMs", DeliveryTrace.Round(Stopwatch.GetElapsedTime(received, decoded).TotalMilliseconds));
            DeliveryTrace.WritePhase(writer, "apply", apply, decoded, projected);
            DeliveryTrace.WritePhase(writer, "proc", process, projected, inbox);
            DeliveryTrace.WritePhase(writer, "lease", lease, leaseStarted, leaseEnded);
            writer.WriteNumber("end", DeliveryTrace.Round(trace.Milliseconds(lease is null ? inbox : leaseEnded)));
            writer.WriteStartArray("ops");
            foreach (var row in events) { writer.WriteStringValue(row.Id.ToString("N")); }
            writer.WriteEndArray();
        });
    }

    private async Task CompleteTraceAsync(long offeredStarted, long offeredEnded, CancellationToken token)
    {
        if (_sampler is not null) { await _sampler.DisposeAsync(); _sampler = null; }
        if (_trace is not { } trace) { return; }
        foreach (var row in _operations.Values.OrderBy(static row => row.Offered))
        {
            trace.Write(writer =>
            {
                writer.WriteString("k", "op");
                writer.WriteString("id", row.Id.ToString("N"));
                writer.WriteNumber("tenant", row.Tenant);
                writer.WriteNumber("order", row.Order);
                writer.WriteBoolean("cust", row.CustomerChange);
                writer.WriteNumber("off", DeliveryTrace.Round(trace.Milliseconds(row.Offered)));
                writer.WriteNumber("com", DeliveryTrace.Round(trace.Milliseconds(Volatile.Read(ref row.Committed))));
                writer.WriteNumber("prj", DeliveryTrace.Round(trace.Milliseconds(Volatile.Read(ref row.Projected))));
                writer.WriteNumber("inb", DeliveryTrace.Round(trace.Milliseconds(Volatile.Read(ref row.Inbox))));
                writer.WriteNumber("live", DeliveryTrace.Round(trace.Milliseconds(Volatile.Read(ref row.Live))));
            });
        }
        await trace.CompleteAsync(_source, writer =>
        {
            writer.WriteNumber("offerStart", DeliveryTrace.Round(trace.Milliseconds(offeredStarted)));
            writer.WriteNumber("offerEnd", DeliveryTrace.Round(trace.Milliseconds(offeredEnded)));
            writer.WriteNumber("walTransactions", Interlocked.Read(ref _walTransactions));
            writer.WriteNumber("operations", _operations.Count);
        }, token);
        await trace.DisposeAsync();
        _trace = null;
    }

    private async ValueTask HandleAsync(StoredEvent value, System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, CancellationToken token)
    {
        await using var command = Sql.Command(connection, transaction, $"INSERT INTO \"{_eventsSchema}\".effects VALUES(@id,@tenant,@sequence)",
            ("id", value.EventId), ("tenant", value.Stream.TenantId), ("sequence", value.Sequence));
        await command.ExecuteNonQueryAsync(token);
    }

    private static LatencySummary Latency(IEnumerable<Operation> operations, Func<Operation, long> timestamp)
    {
        var values = operations.Select(row => (Start: row.Offered, End: timestamp(row))).Where(static row => row.End != 0)
            .Select(static row => Stopwatch.GetElapsedTime(row.Start, row.End).TotalMilliseconds).Order().ToArray();
        return values.Length == 0 ? new(0, 0, 0, 0, 0) : new(values.LongLength, values[(int)Math.Ceiling(values.Length * .5) - 1],
            values[(int)Math.Ceiling(values.Length * .95) - 1], values[(int)Math.Ceiling(values.Length * .99) - 1], values[^1]);
    }

    private async Task ObserveServiceAsync(DatabaseProbe probe, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        void Record()
        {
            Program.Check(_serviceWindows.Count < 512, "service-window telemetry bound");
            static long[] Read(long[] values) => Enumerable.Range(0, values.Length).Select(index => Interlocked.Read(ref values[index])).ToArray();
            var pool = _source.GetPoolStatistics();
            var physical = probe.CurrentPhysical();
            _serviceWindows.Add(new(Stopwatch.GetElapsedTime(started).TotalSeconds, Read(_offered), Read(_rejected),
                Read(_committedByTenant), Read(_deliveredByTenant), Math.Max(0, Volatile.Read(ref _queued)), pool.Busy, pool.Waiting,
                physical.RetainedSlotBytes, physical.OwnedStorageBytes, physical.AgeSeconds));
        }
        try { do { Record(); } while (await timer.WaitForNextTickAsync(token)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Record(); }
    }

    private static async Task MonitorAsync(Task[] workers, CancellationTokenSource pipeline)
    {
        var completed = await Task.WhenAny(workers);
        if (pipeline.IsCancellationRequested) { return; }
        await pipeline.CancelAsync();
        await completed;
        Program.Check(false, "pipeline worker ended before controlled shutdown");
    }

    public async ValueTask DisposeAsync()
    {
        if (_sampler is not null) { await _sampler.DisposeAsync(); }
        if (_trace is not null) { await _trace.DisposeAsync(); }
        await _readers.CancelAsync();
        foreach (var client in _clients) { await client.DisposeAsync(); }
        try { await Task.WhenAll(_clientReaders); }
        finally
        {
            foreach (var subscription in _live) { await subscription.DisposeAsync(); }
            if (_walCancellation is not null) { await _walCancellation.CancelAsync(); }
            if (_walReader is not null) { await _walReader.DisposeAsync(); }
            if (_snapshot is not null) { await _snapshot.DisposeAsync(); }
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await Sql.ExecuteAsync(_source, $"SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name IN ('{_slot}','{_slot}_rebuild'); DROP PUBLICATION IF EXISTS \"{_publication}\"; DROP SCHEMA IF EXISTS \"{_eventsSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", cleanup.Token);
            }
            finally
            {
                _readers.Dispose(); _walCancellation?.Dispose(); await _routing.DisposeAsync(); await _source.DisposeAsync();
                if (_originalSource is not null) { await _originalSource.DisposeAsync(); }
                await _deliveryRouting.DisposeAsync(); await _deliverySource.DisposeAsync();
                if (_originalDeliverySource is not null) { await _originalDeliverySource.DisposeAsync(); }
            }
        }
    }
}

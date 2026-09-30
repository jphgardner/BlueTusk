using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using BlueTusk.Edge.Http;
using BlueTusk.Edge.Server;
using BlueTusk.Edge.Sqlite;
using Microsoft.Data.Sqlite;

namespace BlueTusk.Edge.LoadHarness;

internal sealed class SqliteCapacityClient : IDisposable
{
    private readonly EdgeScope _scope;
    private readonly string _path;
    private readonly int _index;
    private readonly int _payloadBytes;
    private readonly Random _random;
    private readonly MutableTimeProvider _clock;
    private readonly LatencyCapture _enqueue = new(), _ack = new(), _syncPass = new(), _ackScan = new();
    private readonly EdgeLocalPhaseTimings _localPhases = new();
    private readonly Dictionary<string, (Guid Id, long Started, double OfferedAt)> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pendingGate = new(1, 1);
    private readonly List<double> _recoveries = [];
    private SqliteEdgeStore _local;
    private readonly HttpClient _http;
    private readonly HttpEdgeRemoteTransport _remote;
    private readonly FaultTransport _fault;
    private EdgeSynchronizationCoordinator _coordinator;
    private EdgeMutation? _firstMutation;
    private Guid _lostMutation;
    private double _lostAt;
    private bool _lostRecovered;
    private bool _firstOfflineRecovered, _secondOfflineRecovered, _hostRecovered;
    private long _offered, _skipped, _scheduleSkipped, _pendingKeySkipped, _acknowledged, _peakPending, _peakOutbox, _maximumFileBytes;

    private SqliteCapacityClient(int index, string path, int payloadBytes, MutableTimeProvider clock,
        SqliteEdgeStore local, HttpClient http, HttpEdgeRemoteTransport remote, FaultTransport fault)
    {
        _index = index; _scope = new EdgeScope(Tenant(index), "orders", 1); _path = path;
        _payloadBytes = payloadBytes; _clock = clock; _random = new Random(84273 + index);
        _local = local; _http = http; _remote = remote; _fault = fault;
        _coordinator = new EdgeSynchronizationCoordinator(new TimedEdgeLocalStore(local, _localPhases), fault);
    }

    internal static async Task<SqliteCapacityClient> OpenAsync(int index, string directory, Uri endpoint,
        int payloadBytes, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"client-{index.ToString("D2", CultureInfo.InvariantCulture)}.db");
        var clock = new MutableTimeProvider();
        var local = new SqliteEdgeStore(Options(path, clock));
        await local.InitializeAsync(token).ConfigureAwait(false);
        await local.ActivateScopeAsync(new EdgeScope(Tenant(index), "orders", 1), cancellationToken: token).ConfigureAwait(false);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "edge-capacity-" + Tenant(index));
        var remote = new HttpEdgeRemoteTransport(http, endpoint, new EdgeHttpTransportOptions { RequestTimeout = TimeSpan.FromSeconds(30) });
        var fault = new FaultTransport(remote);
        var result = new SqliteCapacityClient(index, path, payloadBytes, clock, local, http, remote, fault);
        await result._coordinator.SynchronizeAsync(result._scope, 16, 16, token).ConfigureAwait(false);
        return result;
    }

    internal EdgeScope Scope => _scope;
    internal async ValueTask<long> CheckpointAsync(CancellationToken token) =>
        (await _local.GetCheckpointAsync(_scope, token).ConfigureAwait(false)).Position;

    internal async Task RunAsync(Stopwatch clock, int seconds, CancellationToken token)
    {
        var interval = TimeSpan.FromMilliseconds(200);
        var offlineSeconds = seconds >= 1800 ? 30.0 : Math.Min(5.0, seconds / 12.0);
        var offlineOne = seconds / 3.0; var lossAt = seconds / 2.0;
        var offlineTwo = seconds * 2.0 / 3.0; var hostAt = seconds * 3.0 / 4.0;
        _fault.LossAt = lossAt; _fault.Clock = clock;
        var plannedSlots = (long)seconds * 1000 / 200;
        long next = 0;
        using var syncCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var synchronizer = SynchronizeWhileOfferingAsync(clock, seconds, offlineOne, offlineTwo,
            offlineSeconds, hostAt, syncCancellation.Token);
        try
        {
            while (next < plannedSlots && clock.Elapsed.TotalSeconds < seconds)
            {
                if (synchronizer.IsFaulted) { await synchronizer.ConfigureAwait(false); }
                token.ThrowIfCancellationRequested();
                var elapsed = clock.Elapsed.TotalSeconds;
                var due = next * interval.TotalSeconds;
                if (due > elapsed)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min((due - elapsed) * 1000, 20)), token).ConfigureAwait(false);
                    continue;
                }
                var current = (long)Math.Floor(elapsed / interval.TotalSeconds);
                if (current > next) { _skipped += current - next; _scheduleSkipped += current - next; next = current; }
                var key = "doc-" + (next % 256).ToString("D3", CultureInfo.InvariantCulture);
                await _pendingGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var cached = await _local.GetAsync(_scope, key, token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The seeded local record is missing.");
                    if (cached.PendingMutationId is not null || _pending.ContainsKey(key)) { _skipped++; _pendingKeySkipped++; }
                    else
                    {
                        var offeredAt = clock.Elapsed.TotalSeconds;
                        var began = Stopwatch.GetTimestamp();
                        var mutation = await _local.EnqueueOrderedAsync(_scope, key, cached.ServerRevision, EdgeMutationKind.Upsert,
                            Payload(_payloadBytes), token).ConfigureAwait(false);
                        _enqueue.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                        _pending.Add(key, (mutation.Id, began, offeredAt)); _offered++;
                        _firstMutation ??= mutation;
                        ObservePeak(ref _peakPending, _pending.Count);
                    }
                }
                finally { _pendingGate.Release(); }
                next++;
            }
            await synchronizer.ConfigureAwait(false);
        }
        finally
        {
            if (!synchronizer.IsCompleted)
            {
                syncCancellation.Cancel();
                try { await synchronizer.ConfigureAwait(false); }
                catch (OperationCanceledException) when (syncCancellation.IsCancellationRequested) { }
            }
        }
        _skipped += plannedSlots - next;
        _scheduleSkipped += plannedSlots - next;
        var drainStarted = Stopwatch.GetTimestamp();
        while (_pending.Count > 0 || await _local.ReadNextUnconfirmedOrderedReceiptAsync(_scope, token).ConfigureAwait(false) is not null ||
            await _local.ReadConfirmedOrderedHorizonAsync(_scope, 64, token).ConfigureAwait(false) is not null)
        {
            if (Stopwatch.GetElapsedTime(drainStarted).TotalSeconds > 120) { throw new InvalidOperationException("SQLite client drain exceeded 120 seconds."); }
            await SynchronizeAsync(clock, offlineOne, offlineTwo, offlineSeconds, hostAt, token).ConfigureAwait(false);
            await Task.Delay(20, token).ConfigureAwait(false);
        }
        ObservePeak(ref _maximumFileBytes, PhysicalBytes());
        var sync = _syncPass.Snapshot();
        var confirm = _fault.Confirm.Snapshot();
        var changes = _fault.Changes.Snapshot();
        Console.WriteLine($"SQLite client {_index}: coordinator passes {sync.Samples} p50/p95/p99 " +
            $"{sync.P50Milliseconds:F1}/{sync.P95Milliseconds:F1}/{sync.P99Milliseconds:F1} ms; " +
            $"remote confirms {confirm.Samples} p50/p95/p99 " +
            $"{confirm.P50Milliseconds:F1}/{confirm.P95Milliseconds:F1}/{confirm.P99Milliseconds:F1} ms; " +
            $"change reads {changes.Samples} p50/p95/p99 " +
            $"{changes.P50Milliseconds:F1}/{changes.P95Milliseconds:F1}/{changes.P99Milliseconds:F1} ms.");
        Console.WriteLine($"SQLite client {_index} local phase p95 ms: " +
            $"checkpoint {_localPhases.Checkpoint.Snapshot().P95Milliseconds:F1}, " +
            $"claim {_localPhases.Claim.Snapshot().P95Milliseconds:F1}, " +
            $"ack {_localPhases.Acknowledge.Snapshot().P95Milliseconds:F1}, " +
            $"receipt read {_localPhases.ReadReceipt.Snapshot().P95Milliseconds:F1}, " +
            $"receipt mark {_localPhases.ConfirmReceipt.Snapshot().P95Milliseconds:F1}, " +
            $"horizon read {_localPhases.ReadHorizon.Snapshot().P95Milliseconds:F1}, " +
            $"horizon mark {_localPhases.AdvanceHorizon.Snapshot().P95Milliseconds:F1}, " +
            $"apply changes {_localPhases.ApplyChanges.Snapshot().P95Milliseconds:F1}.");
        var ackScan = _ackScan.Snapshot();
        Console.WriteLine($"SQLite client {_index} acknowledgement scans {ackScan.Samples} p50/p95/p99 " +
            $"{ackScan.P50Milliseconds:F1}/{ackScan.P95Milliseconds:F1}/{ackScan.P99Milliseconds:F1} ms.");
    }

    internal async Task<ClientReport> ReportAsync(PostgreSqlEdgeServerStore server, CancellationToken token)
    {
        var state = await LocalStateAsync(token).ConfigureAwait(false);
        if (_firstMutation is null) { throw new InvalidOperationException("The client offered no ordered mutation."); }
        bool fenced;
        // A reclaimed identity is permanently fenced even after reopening the transport and store.
        try
        {
            _ = await _remote.ApplyMutationAsync(_firstMutation, token).ConfigureAwait(false);
            fenced = false;
        }
        catch (EdgeHttpTransportException error) when (error.StatusCode == 410) { fenced = true; }
        var exact = true;
        for (var key = 0; key < 256; key++)
        {
            var id = "doc-" + key.ToString("D3", CultureInfo.InvariantCulture);
            var local = await _local.GetAsync(_scope, id, token).ConfigureAwait(false);
            var remote = await server.GetAsync(_scope, id, token).ConfigureAwait(false);
            if (local is null || remote is null || local.PendingMutationId is not null ||
                local.ServerRevision != remote.Revision || !local.Payload.Span.SequenceEqual(remote.Payload.Span))
            { exact = false; break; }
        }
        var checkpoint = await _local.GetCheckpointAsync(_scope, token).ConfigureAwait(false);
        return new(_index, "SQLite", state.StreamId, _offered, _skipped, _scheduleSkipped, _pendingKeySkipped,
            _acknowledged, 0, 0, _peakPending,
            _peakOutbox, state.Pending, state.Outbox, state.Receipts, checkpoint.Position,
            state.NextSequence - 1, state.Horizon, _maximumFileBytes, _enqueue.Snapshot(), _fault.Apply.Snapshot(),
            _ack.Snapshot(), _fault.Horizon.Snapshot(), _recoveries.ToArray(), _lostRecovered, fenced, exact);
    }

    internal async Task SampleAsync(CancellationToken token)
    {
        var state = await LocalStateAsync(token).ConfigureAwait(false);
        ObservePeak(ref _peakPending, state.Pending);
        ObservePeak(ref _peakOutbox, state.Outbox);
        ObservePeak(ref _maximumFileBytes, PhysicalBytes());
    }

    public void Dispose() { _pendingGate.Dispose(); _remote.Dispose(); _http.Dispose(); }

    private static void ObservePeak(ref long peak, long value)
    {
        long observed;
        do
        {
            observed = Volatile.Read(ref peak);
            if (value <= observed) { return; }
        }
        while (Interlocked.CompareExchange(ref peak, value, observed) != observed);
    }

    private async Task SynchronizeWhileOfferingAsync(Stopwatch clock, int seconds, double offlineOne,
        double offlineTwo, double offlineSeconds, double hostAt, CancellationToken token)
    {
        var lastPass = 0.0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            token.ThrowIfCancellationRequested();
            var elapsed = clock.Elapsed.TotalSeconds;
            if (!Offline(elapsed, offlineOne, offlineTwo, offlineSeconds))
            {
                await _pendingGate.WaitAsync(token).ConfigureAwait(false);
                bool pending;
                try { pending = _pending.Count > 0; }
                finally { _pendingGate.Release(); }
                if (pending || elapsed - lastPass >= 1)
                {
                    await SynchronizeAsync(clock, offlineOne, offlineTwo, offlineSeconds, hostAt, token).ConfigureAwait(false);
                    lastPass = clock.Elapsed.TotalSeconds;
                }
            }
            await Task.Delay(20, token).ConfigureAwait(false);
        }
    }

    private async Task SynchronizeAsync(Stopwatch clock, double offlineOne, double offlineTwo,
        double offlineSeconds, double hostAt, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await _coordinator.SynchronizeAsync(_scope, 16, 16, token).ConfigureAwait(false);
        }
        catch (LostResponseException error)
        {
            _lostMutation = error.MutationId; _lostAt = clock.Elapsed.TotalSeconds;
            _clock.Advance(TimeSpan.FromMinutes(2));
            await _pendingGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _local = new SqliteEdgeStore(Options(_path, _clock));
                await _local.InitializeAsync(token).ConfigureAwait(false);
                _coordinator = new EdgeSynchronizationCoordinator(new TimedEdgeLocalStore(_local, _localPhases), _fault);
                var lost = _pending.SingleOrDefault(item => item.Value.Id == _lostMutation);
                var persisted = lost.Key is null ? null : await _local.GetAsync(_scope, lost.Key, token).ConfigureAwait(false);
                if (persisted?.PendingMutationId != _lostMutation)
                { throw new InvalidOperationException("Lost response was not a durable leased mutation."); }
            }
            finally { _pendingGate.Release(); }
            return;
        }
        catch (HttpRequestException) when (clock.Elapsed.TotalSeconds >= hostAt - 2 && clock.Elapsed.TotalSeconds <= hostAt + 30) { return; }
        catch (TaskCanceledException) when (clock.Elapsed.TotalSeconds >= hostAt - 2 && clock.Elapsed.TotalSeconds <= hostAt + 30) { return; }
        finally { _syncPass.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
        await MarkAcknowledgedAsync(clock, offlineOne, offlineTwo, offlineSeconds, hostAt, token).ConfigureAwait(false);
        var elapsed = clock.Elapsed.TotalSeconds;
        if (!_firstOfflineRecovered && elapsed >= offlineOne + offlineSeconds)
        { _recoveries.Add(elapsed - (offlineOne + offlineSeconds)); _firstOfflineRecovered = true; }
        if (!_secondOfflineRecovered && elapsed >= offlineTwo + offlineSeconds)
        { _recoveries.Add(elapsed - (offlineTwo + offlineSeconds)); _secondOfflineRecovered = true; }
        if (!_hostRecovered && elapsed >= hostAt + 2)
        { _recoveries.Add(elapsed - hostAt); _hostRecovered = true; }
    }

    private async Task MarkAcknowledgedAsync(Stopwatch clock, double offlineOne, double offlineTwo,
        double offlineSeconds, double hostAt, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        await _pendingGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_pending.Count == 0) { return; }
            // Read all pending identities in one consistent SQLite snapshot. A per-key
            // GetAsync here adds one new connection and query for every offline write.
            var builder = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT mutation_id FROM mutations WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch";
            command.Parameters.AddWithValue("tenant", _scope.Tenant);
            command.Parameters.AddWithValue("scope", _scope.Id);
            command.Parameters.AddWithValue("epoch", _scope.Epoch);
            var stillPending = new HashSet<Guid>();
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                { stillPending.Add(Guid.ParseExact(reader.GetString(0), "N")); }
            }
            foreach (var item in _pending.ToArray())
            {
                if (stillPending.Contains(item.Value.Id)) { continue; }
                _pending.Remove(item.Key); _acknowledged++;
                if (item.Value.Id == _lostMutation)
                {
                    _recoveries.Add(clock.Elapsed.TotalSeconds - _lostAt);
                    _lostRecovered = true;
                }
                var now = clock.Elapsed.TotalSeconds;
                if (!OverlapsFault(item.Value.OfferedAt, now, offlineOne, offlineTwo, offlineSeconds,
                        hostAt, _lostAt, _lostMutation != Guid.Empty))
                { _ack.Add(Stopwatch.GetElapsedTime(item.Value.Started).TotalMilliseconds); }
            }
        }
        finally
        {
            _pendingGate.Release();
            _ackScan.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task<(long Pending, long Outbox, long Receipts, string StreamId, long NextSequence, long Horizon)> LocalStateAsync(CancellationToken token)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM mutations),(SELECT count(*) FROM ordered_confirmations)," +
            "(SELECT count(*) FROM receipts),(SELECT stream_id FROM ordered_streams LIMIT 1)," +
            "(SELECT next_sequence FROM ordered_streams LIMIT 1),(SELECT horizon FROM ordered_streams LIMIT 1)";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) { throw new InvalidOperationException("Local capacity state is missing."); }
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5));
    }

    private long PhysicalBytes()
    {
        // The main database must remain present. SQLite may unlink WAL and SHM
        // sidecars between a directory observation and reading their length.
        long total = new FileInfo(_path).Length;
        foreach (var path in new[] { _path + "-wal", _path + "-shm" })
        {
            try { total += new FileInfo(path).Length; }
            catch (FileNotFoundException) { }
        }
        return total;
    }

    private byte[] Payload(int bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var chars = new char[bytes];
        chars[0] = '{'; chars[1] = '"'; chars[2] = 'v'; chars[3] = '"'; chars[4] = ':'; chars[5] = '"';
        for (var i = 6; i < bytes - 2; i++) { chars[i] = alphabet[_random.Next(alphabet.Length)]; }
        chars[^2] = '"'; chars[^1] = '}';
        return System.Text.Encoding.UTF8.GetBytes(chars);
    }

    private static string Tenant(int index) => "tenant-" + index.ToString("D2", CultureInfo.InvariantCulture);
    private static bool Offline(double elapsed, double first, double second, double duration) =>
        (elapsed >= first && elapsed < first + duration) || (elapsed >= second && elapsed < second + duration);
    private static bool OverlapsFault(double offered, double acknowledged, double first, double second,
        double duration, double hostAt, double lostAt, bool lost) =>
        Overlaps(offered, acknowledged, first, first + duration) ||
        Overlaps(offered, acknowledged, second, second + duration) ||
        Overlaps(offered, acknowledged, hostAt - 2, hostAt + 30) ||
        (lost && Overlaps(offered, acknowledged, lostAt - 10, lostAt + 10));
    private static bool Overlaps(double offered, double acknowledged, double start, double end) =>
        offered <= end && acknowledged >= start;
    private static SqliteEdgeOptions Options(string path, TimeProvider clock) => new()
    {
        DatabasePath = path,
        TimeProvider = clock,
        MaxRecordBytes = 8192,
        MaxCacheRecords = 512,
        MaxCacheBytes = 4 * 1024 * 1024,
        MaxStagedRecords = 512,
        MaxStagedBytes = 4 * 1024 * 1024,
        MaxPendingMutations = 512,
        MaxPendingBytes = 4 * 1024 * 1024,
        MaxReceiptRecords = 512
    };

    private sealed class MutableTimeProvider : TimeProvider
    {
        private long _offset;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddTicks(Interlocked.Read(ref _offset));
        internal void Advance(TimeSpan span) => Interlocked.Add(ref _offset, span.Ticks);
    }

    private sealed class LostResponseException(Guid mutationId) : IOException("The capacity fixture dropped a committed mutation response.")
    { internal Guid MutationId { get; } = mutationId; }

    private sealed class FaultTransport(HttpEdgeRemoteTransport inner) : IEdgeRemoteTransport, IEdgeOrderedReceiptTransport
    {
        private int _lost;
        internal Stopwatch? Clock { get; set; }
        internal double LossAt { get; set; }
        internal LatencyCapture Apply { get; } = new();
        internal LatencyCapture Horizon { get; } = new();
        internal LatencyCapture Confirm { get; } = new();
        internal LatencyCapture Changes { get; } = new();
        public ValueTask<EdgeSnapshot> BeginSnapshotAsync(EdgeScope scope, CancellationToken token = default) => inner.BeginSnapshotAsync(scope, token);
        public IAsyncEnumerable<IReadOnlyList<EdgeRecord>> ReadSnapshotAsync(EdgeScope scope, EdgeSnapshot snapshot,
            CancellationToken token = default) => inner.ReadSnapshotAsync(scope, snapshot, token);
        public async ValueTask<EdgeChangeBatch?> ReadChangesAsync(EdgeScope scope, long after, int maximum,
            CancellationToken token = default)
        {
            var started = Stopwatch.GetTimestamp();
            var result = await inner.ReadChangesAsync(scope, after, maximum, token).ConfigureAwait(false);
            Changes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        public async ValueTask<EdgeMutationOutcome> ApplyMutationAsync(EdgeMutation mutation, CancellationToken token = default)
        {
            var started = Stopwatch.GetTimestamp();
            var outcome = await inner.ApplyMutationAsync(mutation, token).ConfigureAwait(false);
            Apply.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (Clock is not null && Clock.Elapsed.TotalSeconds >= LossAt && Interlocked.CompareExchange(ref _lost, 1, 0) == 0)
            { throw new LostResponseException(mutation.Id); }
            return outcome;
        }
        public async ValueTask FinalizeMutationReceiptAsync(EdgeMutation mutation, CancellationToken token = default)
        {
            var started = Stopwatch.GetTimestamp();
            await inner.FinalizeMutationReceiptAsync(mutation, token).ConfigureAwait(false);
            Confirm.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        public async ValueTask AdvanceOrderedReceiptHorizonAsync(EdgeScope scope, Guid through, int maximum = 1000,
            CancellationToken token = default)
        {
            var started = Stopwatch.GetTimestamp();
            await inner.AdvanceOrderedReceiptHorizonAsync(scope, through, maximum, token).ConfigureAwait(false);
            Horizon.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}

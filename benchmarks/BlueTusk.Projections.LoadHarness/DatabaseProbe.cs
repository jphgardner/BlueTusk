using System.Diagnostics;
using System.Globalization;
using BlueTusk.Data;

namespace BlueTusk.Projections.LoadHarness;

internal sealed class DatabaseProbe : IAsyncDisposable
{
    private readonly BlueTuskDataSource _source;
    private readonly string _schema;
    private readonly string _events;
    private readonly string _slot;
    private readonly CancellationTokenSource _stop = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly long _allocated = GC.GetTotalAllocatedBytes(true);
    private readonly double _cpu;
    private readonly int[] _collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private readonly Task _sampling;
    private long _workingSet, _managed, _clients, _locks, _waiting, _retained, _storage;
    private long _currentRetained, _currentStorage, _lastPhysicalSample;
    private int _total, _busy, _poolWaiting;
    internal Task Completion => _sampling;

    internal (long RetainedSlotBytes, long OwnedStorageBytes, double? AgeSeconds) CurrentPhysical()
    {
        var sampledAt = Interlocked.Read(ref _lastPhysicalSample);
        return (Interlocked.Read(ref _currentRetained), Interlocked.Read(ref _currentStorage),
            sampledAt == 0 ? null : Stopwatch.GetElapsedTime(sampledAt).TotalSeconds);
    }

    internal DatabaseProbe(BlueTuskDataSource source, string schema, string events, string slot)
    {
        _source = source; _schema = schema; _events = events; _slot = slot;
        _cpu = _process.TotalProcessorTime.TotalMilliseconds;
        _sampling = SampleAsync(_stop.Token);
    }

    internal async Task<RuntimeObservation> FinishAsync()
    {
        await _stop.CancelAsync(); await _sampling;
        _process.Refresh();
        return new(GC.GetTotalAllocatedBytes(true) - _allocated, _process.TotalProcessorTime.TotalMilliseconds - _cpu,
            _workingSet, _managed, GC.CollectionCount(0) - _collections[0], GC.CollectionCount(1) - _collections[1],
            GC.CollectionCount(2) - _collections[2], _total, _busy, _poolWaiting, _clients, _locks, _waiting, _retained, _storage);
    }

    private async Task SampleAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            do
            {
                _process.Refresh(); _workingSet = Math.Max(_workingSet, _process.WorkingSet64);
                _managed = Math.Max(_managed, GC.GetTotalMemory(false));
                var pool = _source.GetPoolStatistics();
                _total = Math.Max(_total, pool.Total); _busy = Math.Max(_busy, pool.Busy); _poolWaiting = Math.Max(_poolWaiting, pool.Waiting);
                await using var connection = await _source.OpenConnectionAsync(token);
                await using var command = Sql.Command(connection, null, """
                    SELECT count(*) FILTER (WHERE backend_type='client backend'),
                        count(*) FILTER (WHERE wait_event_type='Lock'),count(*) FILTER (WHERE wait_event_type IS NOT NULL),
                        coalesce((SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(),restart_lsn)::bigint
                            FROM pg_replication_slots WHERE slot_name=@slot),0),
                        (SELECT coalesce(sum(pg_total_relation_size(c.oid)),0)::bigint FROM pg_class c
                            JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN (@schema,@events) AND c.relkind='r')
                    FROM pg_stat_activity WHERE datname=current_database() AND application_name='BlueTuskProjectionsLoadHarness'
                    """, ("slot", _slot), ("schema", _schema), ("events", _events));
                command.CommandTimeout = 5;
                await using var reader = await command.ExecuteReaderAsync(token);
                Program.Check(await reader.ReadAsync(token), "bounded database probe row");
                _clients = Math.Max(_clients, reader.GetInt64(0)); _locks = Math.Max(_locks, reader.GetInt64(1));
                _waiting = Math.Max(_waiting, reader.GetInt64(2));
                var retained = reader.GetInt64(3);
                var storage = reader.GetInt64(4);
                _retained = Math.Max(_retained, retained);
                _storage = Math.Max(_storage, storage);
                Interlocked.Exchange(ref _currentRetained, retained);
                Interlocked.Exchange(ref _currentStorage, storage);
                Interlocked.Exchange(ref _lastPhysicalSample, Stopwatch.GetTimestamp());
                Program.Check(_retained < 8L * 1024 * 1024 * 1024 && _storage < 12L * 1024 * 1024 * 1024, "owned WAL/storage safety ceiling");
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal static async Task<StorageObservation> StorageAsync(BlueTuskDataSource source, string schema, string events, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        string version;
        int major;
        await using (var command = Sql.Command(connection, null, "SELECT current_setting('server_version'),current_setting('server_version_num')::integer"))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            Program.Check(await reader.ReadAsync(token), "server version observation"); version = reader.GetString(0); major = reader.GetInt32(1) / 10_000;
        }
        var relations = new List<RelationObservation>();
        await using (var command = Sql.Command(connection, null, """
            SELECT schemaname,relname,pg_relation_size(relid),pg_indexes_size(relid),pg_total_relation_size(relid),
                n_live_tup,n_dead_tup,vacuum_count,autovacuum_count,analyze_count,autoanalyze_count
            FROM pg_stat_user_tables WHERE schemaname IN (@schema,@events) ORDER BY schemaname,relname
            """, ("schema", schema), ("events", events)))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                Program.Check(relations.Count < 64, "relation observation bound");
                relations.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                    reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10)));
            }
        }
        var checkpoints = major >= 17 ? "SELECT num_timed,num_requested,write_time,sync_time FROM pg_stat_checkpointer"
            : "SELECT checkpoints_timed,checkpoints_req,checkpoint_write_time,checkpoint_sync_time FROM pg_stat_bgwriter";
        await using var counters = Sql.Command(connection, null, $"SELECT pg_current_wal_insert_lsn()::text,wal_records,wal_fpi,wal_bytes::bigint,c.* FROM pg_stat_wal CROSS JOIN ({checkpoints}) c");
        await using var values = await counters.ExecuteReaderAsync(token);
        Program.Check(await values.ReadAsync(token), "WAL/checkpointer observation");
        var parts = values.GetString(0).Split('/');
        var lsn = (ulong.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 32) | ulong.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new(version, lsn, values.GetInt64(1), values.GetInt64(2), values.GetInt64(3), values.GetInt64(4), values.GetInt64(5),
            values.GetDouble(6), values.GetDouble(7), relations.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _sampling; } finally { _stop.Dispose(); _process.Dispose(); }
    }
}

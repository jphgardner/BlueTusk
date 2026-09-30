using System.Diagnostics;
using BlueTusk.Data;

namespace BlueTusk.Documents.LoadHarness;

internal static partial class Program
{
    private static async Task<DatabaseObservation> ObserveDatabaseAsync(BlueTuskDataSource observer, string schema, CancellationToken token)
    {
        await using var connection = await observer.OpenConnectionAsync(token);
        var relations = new List<RelationObservation>();
        await using (var command = new BlueTuskCommand("""
            SELECT s.relname,pg_total_relation_size(s.relid),pg_relation_size(s.relid),pg_indexes_size(s.relid),
              CASE WHEN c.reltoastrelid=0 THEN 0 ELSE pg_total_relation_size(c.reltoastrelid) END,
              s.n_live_tup,s.n_dead_tup,s.vacuum_count,s.autovacuum_count,s.analyze_count,s.autoanalyze_count
            FROM pg_stat_user_tables s JOIN pg_class c ON c.oid=s.relid WHERE s.schemaname=@schema ORDER BY s.relname
            """, connection) { CommandTimeout = 10 })
        {
            command.Parameters.Add(new BlueTuskParameter<string>(schema) { ParameterName = "schema" });
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                Check(relations.Count < 16, "bounded owned relation observation");
                relations.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                    reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10)));
            }
        }
        await using var counters = new BlueTuskCommand("""
            SELECT pg_current_wal_insert_lsn()::text,w.wal_records,w.wal_fpi,w.wal_bytes::bigint,
              d.xact_commit,d.xact_rollback,d.tup_inserted,d.tup_updated,d.tup_deleted,d.deadlocks,d.blks_read,d.blks_hit,d.temp_bytes
            FROM pg_stat_wal w CROSS JOIN pg_stat_database d WHERE d.datname=current_database()
            """, connection) { CommandTimeout = 10 };
        await using var values = await counters.ExecuteReaderAsync(token); Check(await values.ReadAsync(token), "database counters");
        return new(values.GetString(0), values.GetInt64(1), values.GetInt64(2), values.GetInt64(3), values.GetInt64(4), values.GetInt64(5), values.GetInt64(6),
            values.GetInt64(7), values.GetInt64(8), values.GetInt64(9), values.GetInt64(10), values.GetInt64(11), values.GetInt64(12), relations.ToArray());
    }
}

internal sealed class RuntimeProbe : IAsyncDisposable
{
    private readonly BlueTuskDataSource _source;
    private readonly BlueTuskDataSource _observer;
    private readonly string _application;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly CancellationTokenSource _stop = new();
    private readonly long _allocated = GC.GetTotalAllocatedBytes(precise: true);
    private readonly int[] _collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
    private readonly double _cpu;
    private readonly Task _sampling;
    private long _rss, _managed, _clients, _lockWaiters;
    private int _poolTotal, _poolBusy, _poolWaiters, _samples;
    internal RuntimeProbe(BlueTuskDataSource source, BlueTuskDataSource observer, string application)
    { _source = source; _observer = observer; _application = application; _cpu = _process.TotalProcessorTime.TotalMilliseconds; _sampling = SampleAsync(); }
    internal async Task<RuntimeMetrics> FinishAsync()
    {
        await _stop.CancelAsync(); await _sampling; _process.Refresh();
        return new(GC.GetTotalAllocatedBytes(precise: true) - _allocated, GC.CollectionCount(0) - _collections[0], GC.CollectionCount(1) - _collections[1],
            GC.CollectionCount(2) - _collections[2], _process.TotalProcessorTime.TotalMilliseconds - _cpu, _rss, _managed, _poolTotal, _poolBusy, _poolWaiters,
            _clients, _lockWaiters, _samples, _source.GetPoolStatistics());
    }
    private async Task SampleAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            do
            {
                _process.Refresh(); _rss = Math.Max(_rss, _process.WorkingSet64); _managed = Math.Max(_managed, GC.GetTotalMemory(false));
                var pool = _source.GetPoolStatistics(); _poolTotal = Math.Max(_poolTotal, pool.Total); _poolBusy = Math.Max(_poolBusy, pool.Busy); _poolWaiters = Math.Max(_poolWaiters, pool.Waiting);
                await using var connection = await _observer.OpenConnectionAsync(_stop.Token);
                await using var command = new BlueTuskCommand("""
                    SELECT count(*) FILTER(WHERE backend_type='client backend'),count(*) FILTER(WHERE wait_event_type='Lock')
                    FROM pg_stat_activity WHERE datname=current_database() AND application_name=@application
                    """, connection) { CommandTimeout = 3 };
                command.Parameters.Add(new BlueTuskParameter<string>(_application) { ParameterName = "application" });
                await using var reader = await command.ExecuteReaderAsync(_stop.Token); Program.Check(await reader.ReadAsync(_stop.Token), "workload connection samples");
                _clients = Math.Max(_clients, reader.GetInt64(0)); _lockWaiters = Math.Max(_lockWaiters, reader.GetInt64(1)); _samples++;
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    { await _stop.CancelAsync(); await _sampling; _stop.Dispose(); _process.Dispose(); }
}

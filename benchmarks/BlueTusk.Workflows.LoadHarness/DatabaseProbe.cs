using System.Diagnostics;
using System.Globalization;
using BlueTusk.Data;

namespace BlueTusk.Workflows.LoadHarness;

internal sealed class DatabaseProbe : IAsyncDisposable
{
    private readonly BlueTuskDataSource _source;
    private readonly CancellationTokenSource _stop = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly InstrumentProbe _instruments = new();
    private readonly long _allocationStart = GC.GetTotalAllocatedBytes(precise: true);
    private readonly int _gen0Start = GC.CollectionCount(0);
    private readonly int _gen1Start = GC.CollectionCount(1);
    private readonly int _gen2Start = GC.CollectionCount(2);
    private readonly Task _sampling;
    private long _peakWorkingSet;
    private long _peakManaged;
    private int _peakPoolTotal;
    private int _peakPoolBusy;
    private int _peakPoolWaiting;
    private long _peakClients;
    private long _peakLockWaiters;
    private long _peakWaiting;

    internal DatabaseProbe(BlueTuskDataSource source)
    {
        _source = source;
        _sampling = SampleAsync(_stop.Token);
    }

    internal async Task<RuntimeMetrics> FinishAsync()
    {
        await _stop.CancelAsync();
        await _sampling;
        return new(GC.GetTotalAllocatedBytes(precise: true) - _allocationStart,
            GC.CollectionCount(0) - _gen0Start, GC.CollectionCount(1) - _gen1Start, GC.CollectionCount(2) - _gen2Start,
            _peakWorkingSet, _peakManaged, _peakPoolTotal, _peakPoolBusy, _peakPoolWaiting,
            _peakClients, _peakLockWaiters, _peakWaiting, _source.GetPoolStatistics(), _instruments.Snapshot());
    }

    internal static async Task<DatabaseCounters> CountersAsync(BlueTuskDataSource source, string jobsSchema, string workflowSchema)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new BlueTuskCommand("""
            SELECT pg_current_wal_insert_lsn()::text, xact_commit, xact_rollback, tup_inserted, tup_updated, tup_deleted,
                deadlocks, blks_read, blks_hit, temp_bytes,
                (SELECT coalesce(sum(pg_total_relation_size(c.oid)), 0)::bigint FROM pg_class c
                 JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname IN (@jobs, @workflows) AND c.relkind = 'r')
            FROM pg_stat_database WHERE datname = current_database()
            """, connection);
        command.Parameters.Add(new BlueTuskParameter<string>(jobsSchema) { ParameterName = "jobs" });
        command.Parameters.Add(new BlueTuskParameter<string>(workflowSchema) { ParameterName = "workflows" });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Program.Check(await reader.ReadAsync(CancellationToken.None), "database statistics missing");
        string[] lsn = reader.GetString(0).Split('/');
        ulong wal = (ulong.Parse(lsn[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 32) |
            ulong.Parse(lsn[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new(wal, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10));
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            do
            {
                _process.Refresh();
                _peakWorkingSet = Math.Max(_peakWorkingSet, _process.WorkingSet64);
                _peakManaged = Math.Max(_peakManaged, GC.GetTotalMemory(forceFullCollection: false));
                var pool = _source.GetPoolStatistics();
                _peakPoolTotal = Math.Max(_peakPoolTotal, pool.Total);
                _peakPoolBusy = Math.Max(_peakPoolBusy, pool.Busy);
                _peakPoolWaiting = Math.Max(_peakPoolWaiting, pool.Waiting);
                await using var connection = await _source.OpenConnectionAsync(cancellationToken);
                await using var command = new BlueTuskCommand("""
                    SELECT count(*) FILTER (WHERE backend_type = 'client backend'),
                        count(*) FILTER (WHERE wait_event_type = 'Lock'),
                        count(*) FILTER (WHERE wait_event_type IS NOT NULL)
                    FROM pg_stat_activity WHERE datname = current_database() AND application_name = 'BlueTuskJobsWorkflowLoadHarness'
                    """, connection) { CommandTimeout = 3 };
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                Program.Check(await reader.ReadAsync(cancellationToken), "activity statistics missing");
                _peakClients = Math.Max(_peakClients, reader.GetInt64(0));
                _peakLockWaiters = Math.Max(_peakLockWaiters, reader.GetInt64(1));
                _peakWaiting = Math.Max(_peakWaiting, reader.GetInt64(2));
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Sampling belongs to the scenario's structured lifetime.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _sampling;
        _stop.Dispose();
        _process.Dispose();
        _instruments.Dispose();
    }
}

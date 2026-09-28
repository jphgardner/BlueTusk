using System.Diagnostics;
using System.Globalization;
using BlueTusk.Data;

namespace BlueTusk.Workflows.LoadHarness;

internal static partial class Program
{
    private static async Task<PhysicalStorageObservation> PhysicalStorageAsync(BlueTuskDataSource source,
        string jobsSchema, string workflowSchema, int serverMajor, Process process, double cpuStart,
        long allocatedStart, int[] collectionsStart, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        var relations = await RuntimeRelationsAsync(connection, jobsSchema, workflowSchema, token);
        string checkpoints = serverMajor >= 17
            ? "SELECT num_timed, num_requested, write_time, sync_time FROM pg_stat_checkpointer"
            : "SELECT checkpoints_timed, checkpoints_req, checkpoint_write_time, checkpoint_sync_time FROM pg_stat_bgwriter";
        await using var command = new BlueTuskCommand($"""
            SELECT pg_current_wal_insert_lsn()::text, wal_records, wal_fpi, wal_bytes::bigint,
                c.* FROM pg_stat_wal CROSS JOIN ({checkpoints}) c
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        Check(await reader.ReadAsync(token), "physical storage statistics missing");
        string[] parts = reader.GetString(0).Split('/');
        ulong wal = (ulong.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 32) |
            ulong.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        process.Refresh();
        return new(wal, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetDouble(6), reader.GetDouble(7), process.TotalProcessorTime.TotalMilliseconds - cpuStart,
            process.WorkingSet64, GC.GetTotalMemory(forceFullCollection: false), GC.GetTotalAllocatedBytes(precise: true) - allocatedStart,
            GC.CollectionCount(0) - collectionsStart[0], GC.CollectionCount(1) - collectionsStart[1], GC.CollectionCount(2) - collectionsStart[2],
            source.GetPoolStatistics(), relations);
    }

    private static async Task<IReadOnlyList<RelationObservation>> RuntimeRelationsAsync(BlueTuskConnection connection,
        string jobsSchema, string workflowSchema, CancellationToken token)
    {
        await using var command = new BlueTuskCommand("""
            SELECT s.schemaname, s.relname, pg_total_relation_size(s.relid), pg_relation_size(s.relid), pg_indexes_size(s.relid),
                s.n_live_tup, s.n_dead_tup, s.vacuum_count, s.autovacuum_count, s.analyze_count, s.autoanalyze_count,
                to_char(s.last_vacuum AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
                to_char(s.last_autovacuum AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')
            FROM pg_stat_user_tables s WHERE s.schemaname IN (@jobs, @workflows)
                AND s.relname NOT IN ('effects', 'completion_measurements')
            ORDER BY s.schemaname, s.relname
            """, connection);
        command.Parameters.Add(new BlueTuskParameter<string>(jobsSchema) { ParameterName = "jobs" });
        command.Parameters.Add(new BlueTuskParameter<string>(workflowSchema) { ParameterName = "workflows" });
        await using var reader = await command.ExecuteReaderAsync(token);
        var relations = new List<RelationObservation>();
        while (await reader.ReadAsync(token))
        {
            Check(relations.Count < 64, "bounded owned runtime relation observations");
            relations.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        return relations;
    }
}

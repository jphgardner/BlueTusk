using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Data;

namespace BlueTusk.Documents.LoadHarness;

internal enum MaintenanceProfile { PackageDefaults, ToastVacuumFast }

internal sealed record StorageBudget(MaintenanceProfile Profile, long MaximumDatabaseBytes,
    long MinimumFilesystemAvailableBytes, string FilesystemSamplePath, int IdleDrainSeconds)
{
    internal const int MaximumFilesystemSampleAgeSeconds = 30;
    internal const int MaximumMaintenanceGapSeconds = 45;
    internal const int MaximumFilesystemReadAttempts = 5;
    internal const int MaximumFilesystemSamples = 65;
    internal static StorageBudget Read()
    {
        var profile = Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_MAINTENANCE_PROFILE") ?? "PackageDefaults";
        if (!Enum.TryParse<MaintenanceProfile>(profile, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed))
        { throw new InvalidOperationException("Unknown Documents fixture maintenance profile."); }
        var maximum = ReadBytes("BLUETUSK_DOCUMENTS_LOAD_MAX_DATABASE_BYTES", 24L * 1024 * 1024 * 1024);
        var minimum = ReadBytes("BLUETUSK_DOCUMENTS_LOAD_MIN_FILESYSTEM_BYTES", 8L * 1024 * 1024 * 1024);
        var sample = Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_FILESYSTEM_SAMPLE")
            ?? throw new InvalidOperationException("An owned Docker filesystem observer is required for this resource-bounded campaign.");
        var drain = int.Parse(Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_IDLE_DRAIN_SECONDS") ?? "120", CultureInfo.InvariantCulture);
        if (drain is < 0 or > 300) { throw new ArgumentOutOfRangeException(nameof(drain)); }
        return new(parsed, maximum, minimum, Path.GetFullPath(sample), drain);
    }
    private static long ReadBytes(string name, long fallback)
    {
        var value = long.Parse(Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (value is < 1024 * 1024 or > 1024L * 1024 * 1024 * 1024) { throw new ArgumentOutOfRangeException(name); }
        return value;
    }
    internal async Task<FilesystemObservation> ReadFilesystemAsync(CancellationToken token)
    {
        string? selectedNewest = null;
        for (var attempt = 1; attempt <= MaximumFilesystemReadAttempts; attempt++)
        {
            FilesystemObservation observation;
            try
            {
                if (selectedNewest is null)
                {
                    var samples = 0;
                    string? newest = null;
                    foreach (var candidate in Directory.EnumerateFiles(FilesystemSamplePath, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileName(candidate);
                        if (name.Length != 11 || name[6..] != ".json" || !name[..6].All(char.IsAsciiDigit))
                        { throw new JsonException("Owned observer sample name is invalid."); }
                        if (++samples > MaximumFilesystemSamples)
                        { throw new JsonException("Owned observer sample count exceeds its bound."); }
                        if (newest is null || string.CompareOrdinal(candidate, newest) > 0) { newest = candidate; }
                    }
                    selectedNewest = newest;
                }
                if (selectedNewest is null) { throw new FileNotFoundException("Owned observer has no completed samples."); }
                await using var stream = new FileStream(selectedNewest, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.Asynchronous,
                    BufferSize = 4096
                });
                if (stream.Length is <= 0 or > 4096) { throw new JsonException("Owned observer sample length is invalid."); }
                var bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, token);
                observation = JsonSerializer.Deserialize(bytes, StorageJson.Default.FilesystemObservation)
                    ?? throw new JsonException("Owned observer sample is empty.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                if (attempt == MaximumFilesystemReadAttempts)
                { throw new StorageObservationException(exception is JsonException ? "filesystem-observer-invalid" : "filesystem-observer-unavailable"); }
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
                continue;
            }
            var age = DateTimeOffset.UtcNow - observation.ObservedUtc;
            if (age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromSeconds(MaximumFilesystemSampleAgeSeconds))
            { throw new StorageObservationException("filesystem-observer-stale"); }
            if (observation.MinimumAvailableBytesObserved < MinimumFilesystemAvailableBytes)
            { throw new StorageBudgetException("filesystem-headroom", observation.MinimumAvailableBytesObserved, MinimumFilesystemAvailableBytes); }
            return observation with { ReadAttempts = attempt };
        }
        throw new StorageObservationException("filesystem-observer-unavailable");
    }
}

internal sealed class StorageBudgetException(string limit, long observed, long configured, MaintenanceObservation? snapshot = null)
    : Exception("Owned Documents fixture resource limit reached: " + limit)
{
    internal string Limit { get; } = limit;
    internal long Observed { get; } = observed;
    internal long Configured { get; } = configured;
    internal MaintenanceObservation? Snapshot { get; } = snapshot;
}

internal sealed class StorageObservationException(string code, ObserverDiagnostics? diagnostics = null) : Exception("Owned Documents fixture observation failed: " + code)
{
    internal string Code { get; } = code;
    internal ObserverDiagnostics? Diagnostics { get; } = diagnostics;
}

internal sealed class OwnedSchemaCleanupException(string schema, string previousOperation, Exception? previousFailure,
    Exception cleanupFailure, CleanupDiagnostics diagnostics)
    : Exception("Owned Documents schema cleanup failed after bounded attempts.", cleanupFailure)
{
    internal string Schema { get; } = schema;
    internal string PreviousOperation { get; } = previousOperation;
    internal Exception? PreviousFailure { get; } = previousFailure;
    internal CleanupDiagnostics Diagnostics { get; } = diagnostics;
}

internal sealed record FilesystemObservation(DateTimeOffset ObservedUtc, long CapacityBytes, long UsedBytes, long AvailableBytes,
    long MinimumAvailableBytesObserved, long MaximumUsedBytesObserved)
{
    public int ReadAttempts { get; init; }
}
internal sealed record RelationMaintenanceObservation(long RelationId, long TotalBytes, long HeapBytes, long IndexBytes,
    long LiveRows, long DeadRows, long VacuumCount, long AutoVacuumCount, string LastVacuum, string LastAutoVacuum);
internal sealed record VacuumProgressObservation(long RelationId, int ProcessId, string Phase, long HeapBlocksTotal,
    long HeapBlocksScanned, long HeapBlocksVacuumed, long IndexVacuumCount, long DeadTupleBytes, long MaximumDeadTupleBytes, long DeadItemIds);
// Documents.TotalBytes already includes the TOAST relation; Toast.TotalBytes is a breakdown, never additive.
internal sealed record MaintenanceObservation(DateTimeOffset DatabaseTime, long DatabaseBytes,
    RelationMaintenanceObservation Documents, RelationMaintenanceObservation Toast,
    string DocumentsRelOptions, string ToastRelOptions, VacuumProgressObservation[] Vacuums,
    FilesystemObservation Filesystem);

[JsonSerializable(typeof(FilesystemObservation))]
[JsonSerializable(typeof(MaintenanceObservation))]
internal sealed partial class StorageJson : JsonSerializerContext;

internal static partial class Program
{
    private static async Task ApplyMaintenanceProfileAsync(BlueTuskDataSource observer, string schema, StorageBudget budget, CancellationToken token)
    {
        Check(schema.StartsWith("docs_load_", StringComparison.Ordinal) && schema.Length == 42 &&
            schema[10..].All(static character => char.IsAsciiHexDigit(character)), "fixture-only maintenance scope");
        if (budget.Profile is MaintenanceProfile.PackageDefaults) { return; }
        // Explicit experimental fixture settings; no DocumentStore or package defaults change.
        await using var connection = await observer.OpenConnectionAsync(token);
        await using var command = new BlueTuskCommand($"""
            ALTER TABLE "{schema}".documents SET (
              autovacuum_vacuum_scale_factor=0.01,autovacuum_vacuum_threshold=50,
              autovacuum_vacuum_cost_delay=0,autovacuum_vacuum_cost_limit=2000,
              toast.autovacuum_vacuum_scale_factor=0.01,toast.autovacuum_vacuum_threshold=50,
              toast.autovacuum_vacuum_cost_delay=0,toast.autovacuum_vacuum_cost_limit=2000)
            """, connection) { CommandTimeout = 5 };
        _ = await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<MaintenanceObservation> ObserveMaintenanceAsync(BlueTuskDataSource observer, string schema, StorageBudget budget, CancellationToken token)
    {
        var filesystem = await budget.ReadFilesystemAsync(token);
        await using var connection = await observer.OpenConnectionAsync(token);
        await using var command = new BlueTuskCommand("""
            SELECT clock_timestamp(),pg_database_size(current_database()),c.oid::bigint,
              pg_total_relation_size(c.oid),pg_relation_size(c.oid),pg_indexes_size(c.oid),
              COALESCE(s.n_live_tup,0),COALESCE(s.n_dead_tup,0),COALESCE(s.vacuum_count,0),COALESCE(s.autovacuum_count,0),
              COALESCE(s.last_vacuum::text,''),COALESCE(s.last_autovacuum::text,''),t.oid::bigint,
              pg_total_relation_size(t.oid),pg_relation_size(t.oid),pg_indexes_size(t.oid),
              COALESCE(ts.n_live_tup,0),COALESCE(ts.n_dead_tup,0),COALESCE(ts.vacuum_count,0),COALESCE(ts.autovacuum_count,0),
              COALESCE(ts.last_vacuum::text,''),COALESCE(ts.last_autovacuum::text,''),
              COALESCE(array_to_string(c.reloptions,','),''),COALESCE(array_to_string(t.reloptions,','),'')
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_class t ON t.oid=c.reltoastrelid
              LEFT JOIN pg_stat_all_tables s ON s.relid=c.oid LEFT JOIN pg_stat_all_tables ts ON ts.relid=t.oid
            WHERE n.nspname=@schema AND c.relname='documents'
            """, connection) { CommandTimeout = 5 };
        command.Parameters.Add(new BlueTuskParameter<string>(schema) { ParameterName = "schema" });
        DateTimeOffset databaseTime; long databaseBytes; RelationMaintenanceObservation documents; RelationMaintenanceObservation toast;
        string documentsOptions; string toastOptions;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            Check(await reader.ReadAsync(token), "owned document and TOAST relation observation");
            databaseTime = reader.GetFieldValue<DateTimeOffset>(0); databaseBytes = reader.GetInt64(1);
            documents = ReadRelation(reader, 2); toast = ReadRelation(reader, 12);
            documentsOptions = reader.GetString(22); toastOptions = reader.GetString(23);
            Check(!await reader.ReadAsync(token), "singleton owned relation observation");
        }
        var vacuums = new List<VacuumProgressObservation>();
        await using var progress = new BlueTuskCommand("""
            SELECT relid::bigint,pid,phase,COALESCE(heap_blks_total,0),COALESCE(heap_blks_scanned,0),
              COALESCE(heap_blks_vacuumed,0),COALESCE(index_vacuum_count,0),COALESCE(dead_tuple_bytes,0),
              COALESCE(max_dead_tuple_bytes,0),COALESCE(num_dead_item_ids,0)
            FROM pg_stat_progress_vacuum WHERE datname=current_database() AND (relid::bigint=@documents OR relid::bigint=@toast) ORDER BY relid
            """, connection) { CommandTimeout = 5 };
        progress.Parameters.Add(new BlueTuskParameter<long>(documents.RelationId) { ParameterName = "documents" });
        progress.Parameters.Add(new BlueTuskParameter<long>(toast.RelationId) { ParameterName = "toast" });
        await using (var reader = await progress.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                Check(vacuums.Count < 2, "bounded owned vacuum observation");
                vacuums.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                    reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9)));
            }
        }
        if (budget.Profile is MaintenanceProfile.ToastVacuumFast)
        {
            Check(documentsOptions.Contains("autovacuum_vacuum_cost_limit=2000", StringComparison.Ordinal) &&
                documentsOptions.Contains("autovacuum_vacuum_scale_factor=0.01", StringComparison.Ordinal) &&
                toastOptions.Contains("autovacuum_vacuum_cost_limit=2000", StringComparison.Ordinal) &&
                toastOptions.Contains("autovacuum_vacuum_scale_factor=0.01", StringComparison.Ordinal), "actual fixture-only parent/TOAST reloptions");
        }
        var observation = new MaintenanceObservation(databaseTime, databaseBytes, documents, toast, documentsOptions, toastOptions,
            vacuums.ToArray(), filesystem);
        if (databaseBytes >= budget.MaximumDatabaseBytes)
        { throw new StorageBudgetException("database-physical-bytes", databaseBytes, budget.MaximumDatabaseBytes, observation); }
        return observation;
    }
    private static RelationMaintenanceObservation ReadRelation(System.Data.Common.DbDataReader reader, int offset) =>
        new(reader.GetInt64(offset), reader.GetInt64(offset + 1), reader.GetInt64(offset + 2), reader.GetInt64(offset + 3),
            reader.GetInt64(offset + 4), reader.GetInt64(offset + 5), reader.GetInt64(offset + 6), reader.GetInt64(offset + 7),
            reader.GetString(offset + 8), reader.GetString(offset + 9));
}

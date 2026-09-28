using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using BlueTusk.Data;

namespace BlueTusk.Documents.LoadHarness;

internal static partial class Program
{
    private sealed class WriterState(int writer, int[] tenants, int rows)
    {
        internal int Writer { get; } = writer;
        internal int[] Tenants { get; } = tenants;
        internal Dictionary<(int Tenant, int Row), int> Counts { get; } = tenants.SelectMany(tenant => Enumerable.Range(0, rows).Select(row => (tenant, row))).ToDictionary(key => key, _ => 0);
    }
    private static string Tenant(int tenant) => "tenant-" + tenant.ToString("D2", CultureInfo.InvariantCulture);
    private static string Id(int writer, int row) => $"writer-{writer:D2}-row-{row:D2}";

    private static async Task<ScenarioReport> RunScenarioAsync(string raw, BlueTuskDataSource observer, int bytes, int tenants, int writers,
        int seconds, bool sustained, StorageBudget budget, CancellationToken token)
    {
        var schema = "docs_load_" + Guid.NewGuid().ToString("N");
        var pool = Math.Min(writers, 8); const int rows = 8;
        var app = Application + "-" + Guid.NewGuid().ToString("N");
        var observerFault = Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_OBSERVER_FAULT") ?? "None";
        Check(observerFault is "None" or "Once" or "Three", "bounded observer fault mode");
        var postWriteFault = Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_POST_WRITE_FAULT") ?? "None";
        Check(postWriteFault is "None" or "Timeout", "bounded post-write fault mode");
        var cleanupFault = Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_CLEANUP_FAULT") ?? "None";
        Check(cleanupFault is "None" or "Once" or "Three", "bounded cleanup fault mode");
        Check(System.Text.Encoding.UTF8.GetByteCount(app) <= 63, "untruncated PostgreSQL application identity");
        await using var source = CreateSource(raw, app, pool);
        await using var store = new DocumentStore(source, new() { Schema = schema, CommandsPerBatch = 8, MaxSessionOperations = 32, MaxSessionBytes = 16 * 1024 * 1024, MaxPageSize = 32, MaxPageBytes = 8 * 1024 * 1024 });
        var payload = Payload(bytes);
        var states = Enumerable.Range(0, writers).Select(writer => new WriterState(writer,
            Enumerable.Range(0, tenants).Select(step => (writer + step * writers) % tenants).Distinct().ToArray(), rows)).ToArray();
        // The sustained fixture uses a different stable payload for every retained document.
        // Sharing one payload would let a future content-addressed store appear to save space
        // through cross-document deduplication rather than reuse of each document's own content.
        var distinctPayloads = sustained
            ? states.SelectMany(state => state.Tenants.SelectMany(tenant => Enumerable.Range(0, rows)
                .Select(row => (Tenant: tenant, Writer: state.Writer, Row: row))))
                .ToDictionary(key => key, key => Payload(bytes,
                    1 + key.Tenant * 1_000_003 + key.Writer * 10_007 + key.Row * 101))
            : null;
        string PayloadFor(int tenant, int writer, int row) =>
            distinctPayloads is null ? payload : distinctPayloads[(tenant, writer, row)];
        var scenarioOperation = "initialize";
        Exception? primaryFailure = null;
        async Task<ScenarioReport> ExecuteScenarioAsync()
        {
            await store.InitializeAsync(token);
            await ApplyMaintenanceProfileAsync(observer, schema, budget, token);
            _ = await ObserveMaintenanceAsync(observer, schema, budget, token);
            await using (var connection = await source.OpenConnectionAsync(token))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SHOW application_name";
                Check((string?)await command.ExecuteScalarAsync(token) == app, "server application identity matches activity observer");
            }
            scenarioOperation = "seed";
            foreach (var state in states)
                foreach (var tenant in state.Tenants)
                {
                    using var seed = store.OpenSession(Tenant(tenant));
                    for (var row = 0; row < rows; row++)
                    { seed.Insert(Collection, Id(state.Writer, row), new(Tenant(tenant), PayloadFor(tenant, state.Writer, row), 0)); }
                    Check((await seed.SaveChangesAsync(token)).Count == rows, "seed atomic batch");
                    _ = await ObserveMaintenanceAsync(observer, schema, budget, token);
                }
            for (var tenant = 0; tenant < tenants; tenant++)
            {
                using var seed = store.OpenSession(Tenant(tenant)); seed.Insert(Collection, "hot-key", new(Tenant(tenant), Payload(1024), 0));
                _ = await seed.SaveChangesAsync(token);
            }
            var rejected = await VerifyBoundedAdmissionAsync(store, source, token);
            var load = new LatencyHistogram(); var save = new LatencyHistogram(); var operation = new LatencyHistogram(); var hotLatency = new LatencyHistogram();
            var tenantProgress = new long[tenants]; var hotExpected = new long[tenants];
            long committed = 0, replaces = 0, patches = 0, deletes = 0, hotConflicts = 0;
            var samples = new List<StorageSample>(); var maintenanceSamples = new List<MaintenanceSample>();
            var before = await ObserveDatabaseAsync(observer, schema, token);
            var beforeMaintenance = await ObserveMaintenanceAsync(observer, schema, budget, token);
            var lastMaintenanceStamp = Stopwatch.GetTimestamp();
            double maximumMaintenanceGap = 0;
            var databaseProbeTimeouts = 0; var consecutiveDatabaseProbeTimeouts = 0; var maximumConsecutiveDatabaseProbeTimeouts = 0;
            await using var probe = new RuntimeProbe(source, observer, app);
            var start = Stopwatch.GetTimestamp();
            scenarioOperation = "measured-writes";
            using var scenarioStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Exception? sampleFailure = null;
            var storageSampling = SampleStorageAsync();
            async Task SampleStorageAsync()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                try
                {
                    while (await timer.WaitForNextTickAsync(scenarioStop.Token))
                    {
                        Check(samples.Count < 5000, "bounded storage time series");
                        Check(maintenanceSamples.Count < 5000, "bounded maintenance time series");
                        MaintenanceObservation maintenance;
                        try { maintenance = await ObserveMaintenanceAsync(observer, schema, budget, scenarioStop.Token); }
                        catch (TimeoutException)
                        { throw new StorageObservationException("maintenance-probe-timeout", new(databaseProbeTimeouts, maximumConsecutiveDatabaseProbeTimeouts, maximumMaintenanceGap)); }
                        var gap = Stopwatch.GetElapsedTime(lastMaintenanceStamp).TotalSeconds;
                        maximumMaintenanceGap = Math.Max(maximumMaintenanceGap, gap);
                        lastMaintenanceStamp = Stopwatch.GetTimestamp();
                        if (gap > StorageBudget.MaximumMaintenanceGapSeconds)
                        { throw new StorageObservationException("maintenance-gap", new(databaseProbeTimeouts, maximumConsecutiveDatabaseProbeTimeouts, maximumMaintenanceGap)); }
                        maintenanceSamples.Add(new(Stopwatch.GetElapsedTime(start).TotalSeconds, maintenance));
                        try
                        {
                            if (sustained && (observerFault == "Three" || observerFault == "Once" && maintenanceSamples.Count == 1))
                            { await InjectObserverSqlTimeoutAsync(observer, scenarioStop.Token); }
                            var database = await ObserveDatabaseAsync(observer, schema, scenarioStop.Token);
                            samples.Add(new(Stopwatch.GetElapsedTime(start).TotalSeconds, database));
                            consecutiveDatabaseProbeTimeouts = 0;
                            if (sustained && samples.Count % 6 == 0)
                            { Console.WriteLine($"Documents churn {samples[^1].ElapsedSeconds:F0}s: {Interlocked.Read(ref committed)} transitions, {database.Relations.Sum(relation => relation.TotalBytes)} relation bytes, {database.Relations.Sum(relation => relation.DeadRows)} estimated dead rows."); }
                        }
                        catch (TimeoutException)
                        {
                            databaseProbeTimeouts++;
                            consecutiveDatabaseProbeTimeouts++;
                            maximumConsecutiveDatabaseProbeTimeouts = Math.Max(maximumConsecutiveDatabaseProbeTimeouts, consecutiveDatabaseProbeTimeouts);
                            if (consecutiveDatabaseProbeTimeouts >= 3)
                            { throw new StorageObservationException("database-probe-repeated-timeout", new(databaseProbeTimeouts, maximumConsecutiveDatabaseProbeTimeouts, maximumMaintenanceGap)); }
                        }
                    }
                }
                catch (OperationCanceledException) when (scenarioStop.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    sampleFailure = exception;
                    await scenarioStop.CancelAsync();
                }
            }
            try
            {
                await Parallel.ForEachAsync(states, new ParallelOptions { MaxDegreeOfParallelism = writers, CancellationToken = scenarioStop.Token }, async (state, cancellationToken) =>
                {
                    var batchRows = bytes >= 1024 * 1024 ? 2 : 4;
                    for (var iteration = 0; (iteration < state.Tenants.Length || Stopwatch.GetElapsedTime(start).TotalSeconds < seconds) && iteration < 100_000; iteration++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var tenant = state.Tenants[iteration % state.Tenants.Length];
                        var firstRow = ((iteration / state.Tenants.Length) % (rows / batchRows)) * batchRows;
                        var current = new StoredDocument<PayloadDocument>[batchRows]; var loaded = Stopwatch.GetTimestamp();
                        for (var row = 0; row < batchRows; row++)
                        {
                            current[row] = await store.LoadAsync(Tenant(tenant), Collection, Id(state.Writer, firstRow + row), cancellationToken)
                                ?? throw new InvalidOperationException("Bounded writer key vanished.");
                            Check(current[row].Value.Count == state.Counts[(tenant, firstRow + row)] && current[row].Value.TenantMarker == Tenant(tenant), "writer reads own exact version");
                        }
                        load.Add(loaded); var saving = Stopwatch.GetTimestamp();
                        var deletion = iteration % 10 == 9;
                        using (var session = store.OpenSession(Tenant(tenant)))
                        {
                            foreach (var document in current)
                            {
                                if (deletion) { session.Delete(Collection, document.Id, document.Revision); }
                                else if (iteration % 5 == 4)
                                { session.Patch(Collection, document.Id, document.Revision, [DocumentPatch.Set(["Count"], JsonSerializer.SerializeToElement(document.Value.Count + 1, HarnessJson.Default.Int32))]); }
                                else { session.Replace(Collection, document.Id, document.Value with { Count = document.Value.Count + 1 }, document.Revision); }
                            }
                            Check((await session.SaveChangesAsync(cancellationToken)).Count == batchRows, "atomic mixed save");
                        }
                        if (deletion)
                        {
                            using var replacement = store.OpenSession(Tenant(tenant));
                            foreach (var document in current) { replacement.Insert(Collection, document.Id, document.Value with { Count = document.Value.Count + 1 }); }
                            var results = await replacement.SaveChangesAsync(cancellationToken);
                            for (var row = 0; row < batchRows; row++) { Check(results[row].Revision > current[row].Revision, "delete/reinsert ABA fence"); }
                            Interlocked.Increment(ref deletes);
                        }
                        else if (iteration % 5 == 4) { Interlocked.Increment(ref patches); }
                        else { Interlocked.Increment(ref replaces); }
                        save.Add(saving);
                        for (var row = 0; row < batchRows; row++) { state.Counts[(tenant, firstRow + row)]++; }
                        Interlocked.Add(ref committed, batchRows); Interlocked.Add(ref tenantProgress[tenant], batchRows); operation.Add(started);
                    }
                });
            }
            catch (OperationCanceledException) when (sampleFailure is not null)
            {
                ExceptionDispatchInfo.Capture(sampleFailure).Throw();
            }
            finally { await scenarioStop.CancelAsync(); await storageSampling; }
            if (sampleFailure is not null) { ExceptionDispatchInfo.Capture(sampleFailure).Throw(); }
            var measuredSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            scenarioOperation = "post-write-observation";
            if (sustained && postWriteFault == "Timeout") { throw new TimeoutException("Injected post-write workload timeout."); }
            var runtime = await probe.FinishAsync(); var after = await ObserveDatabaseAsync(observer, schema, token);
            var afterMaintenance = await ObserveMaintenanceAsync(observer, schema, budget, token);
            Check(runtime.PeakPoolTotal <= pool && runtime.PeakDatabaseClients is > 0 && runtime.PeakDatabaseClients <= pool, "observed configured physical connection cap");
            // Separately timed contention exercise; its 1 KiB counters are excluded from payload throughput and runtime/WAL phase deltas.
            scenarioOperation = "hot-key-contention";
            var hotStart = Stopwatch.GetTimestamp();
            await Parallel.ForEachAsync(Enumerable.Range(0, writers), new ParallelOptions { MaxDegreeOfParallelism = writers, CancellationToken = token }, async (writer, cancellationToken) =>
            {
                for (var iteration = 0; iteration < 256 && Stopwatch.GetElapsedTime(hotStart).TotalSeconds < Math.Min(seconds, 2); iteration++)
                {
                    var tenant = (writer + iteration * writers) % tenants; var started = Stopwatch.GetTimestamp(); var applied = false;
                    for (var attempt = 0; attempt < 256 && !applied; attempt++)
                    {
                        var current = (await store.LoadAsync(Tenant(tenant), Collection, "hot-key", cancellationToken))!;
                        using var session = store.OpenSession(Tenant(tenant)); session.Replace(Collection, "hot-key", current.Value with { Count = current.Value.Count + 1 }, current.Revision);
                        try { _ = await session.SaveChangesAsync(cancellationToken); applied = true; Interlocked.Increment(ref hotExpected[tenant]); }
                        catch (DocumentConcurrencyException) { Interlocked.Increment(ref hotConflicts); }
                    }
                    Check(applied, "bounded hot-key retry made progress"); hotLatency.Add(started);
                }
            });
            scenarioOperation = "verify-retained-records";
            await VerifyStateAsync(store, states, PayloadFor, tenants, hotExpected, token);
            var afterVerification = await ObserveMaintenanceAsync(observer, schema, budget, token);
            Check(tenantProgress.All(value => value > 0), "all configured tenants progress");
            var idleSamples = new List<MaintenanceSample>();
            MaintenanceObservation? afterIdle = null;
            if (sustained && budget.IdleDrainSeconds > 0)
            {
                scenarioOperation = "idle-drain";
                var idleStarted = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(idleStarted).TotalSeconds < budget.IdleDrainSeconds)
                {
                    var remaining = TimeSpan.FromSeconds(budget.IdleDrainSeconds) - Stopwatch.GetElapsedTime(idleStarted);
                    if (remaining <= TimeSpan.Zero) { break; }
                    await Task.Delay(remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5), token);
                    Check(idleSamples.Count < 64, "bounded idle maintenance time series");
                    afterIdle = await ObserveMaintenanceAsync(observer, schema, budget, token);
                    idleSamples.Add(new(Stopwatch.GetElapsedTime(idleStarted).TotalSeconds, afterIdle));
                }
            }
            var result = new ScenarioReport(sustained ? "sustained-fixed-cardinality-churn" : "payload-tenant-concurrency-sweep", bytes, tenants, writers, pool, rows,
                measuredSeconds, committed, committed / measuredSeconds, committed * (double)bytes / measuredSeconds, tenantProgress, replaces, patches, deletes,
                hotExpected.Sum(), hotConflicts, rejected, load.Snapshot(), save.Snapshot(), operation.Snapshot(), hotLatency.Snapshot(), runtime, before, after, samples.ToArray(), true,
                new(beforeMaintenance, afterMaintenance, afterVerification, afterIdle, maintenanceSamples.ToArray(), idleSamples.ToArray(),
                    new(databaseProbeTimeouts, maximumConsecutiveDatabaseProbeTimeouts, maximumMaintenanceGap)),
                sustained ? "distinct-stable-per-document" : "shared-within-scenario");
            if (sustained)
            {
                scenarioOperation = "persist-sustained-checkpoint";
                var output = Path.GetFullPath(Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_REPORT") ?? "artifacts/documents-load/report.json");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllBytesAsync(Path.ChangeExtension(output, ".sustained-checkpoint.json"),
                    JsonSerializer.SerializeToUtf8Bytes(result, HarnessJson.Default.ScenarioReport), token);
            }
            return result;
        }
        ScenarioReport? scenarioResult = null;
        try { scenarioResult = await ExecuteScenarioAsync(); }
        catch (Exception exception)
        {
            primaryFailure = exception;
            exception.Data["DocumentsOperation"] = scenarioOperation;
        }
        try { await DropSchemaAsync(observer, schema, sustained ? cleanupFault : "None"); }
        catch (Exception cleanupFailure)
        {
            var diagnostics = await CaptureCleanupDiagnosticsAsync(observer, schema);
            throw new OwnedSchemaCleanupException(schema, scenarioOperation, primaryFailure, cleanupFailure, diagnostics);
        }
        if (primaryFailure is not null) { ExceptionDispatchInfo.Capture(primaryFailure).Throw(); }
        return scenarioResult ?? throw new InvalidOperationException("Owned scenario produced no verified result.");
    }

    private static async Task InjectObserverSqlTimeoutAsync(BlueTuskDataSource observer, CancellationToken token)
    {
        await using var connection = await observer.OpenConnectionAsync(token);
        await using var command = new BlueTuskCommand("SELECT pg_sleep(2)", connection) { CommandTimeout = 1 };
        _ = await command.ExecuteScalarAsync(token);
        throw new InvalidOperationException("Observer timeout fault injection did not time out.");
    }

    private static async Task VerifyStateAsync(DocumentStore store, WriterState[] states,
        Func<int, int, int, string> payloadFor, int tenants, long[] hotExpected, CancellationToken token)
    {
        for (var tenant = 0; tenant < tenants; tenant++)
        {
            var expected = states.Where(state => state.Tenants.Contains(tenant)).SelectMany(state => state.Counts.Where(entry => entry.Key.Tenant == tenant)
                .Select(entry => (Id: Id(state.Writer, entry.Key.Row), Count: entry.Value,
                    Payload: payloadFor(tenant, state.Writer, entry.Key.Row))))
                .ToDictionary(entry => entry.Id, entry => (entry.Count, entry.Payload), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
            do
            {
                var page = await store.ReadPageAsync(Tenant(tenant), Collection, 32, cursor, cancellationToken: token);
                foreach (var value in page.Items)
                {
                    Check(seen.Add(value.Id) && value.Value.TenantMarker == Tenant(tenant), "no duplicate/cross-tenant page");
                    if (value.Id == "hot-key") { Check(value.Value.Count == hotExpected[tenant], "no lost hot-key successful increments"); }
                    else
                    {
                        Check(expected.TryGetValue(value.Id, out var item) && item.Count == value.Value.Count &&
                        item.Payload == value.Value.Payload, "exact payload/version after bounded pages");
                    }
                }
                Check(page.NextAfterId is null || page.Items.Count > 0, "bounded pagination progresses"); cursor = page.NextAfterId;
            } while (cursor is not null);
            Check(seen.Count == expected.Count + 1, "fixed-cardinality exact committed row count");
        }
    }
    private static async Task<long> VerifyBoundedAdmissionAsync(DocumentStore store, BlueTuskDataSource source, CancellationToken token)
    {
        await using var bounded = new DocumentStore(source, store.Options with { MaxSessionOperations = 1 });
        using var session = bounded.OpenSession("capacity-test"); session.Insert(Collection, "first", new("capacity-test", "one", 0));
        var rejected = false;
        try { session.Insert(Collection, "second", new("capacity-test", "two", 0)); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "session operation admission rejects without a database write"); session.Clear();
        Check(await store.LoadAsync("capacity-test", Collection, "first", token) is null && await store.LoadAsync("capacity-test", Collection, "second", token) is null, "rejected staged session remains uncommitted");
        return 1;
    }
    internal static async Task DropSchemaAsync(BlueTuskDataSource observer, string schema, string faultMode = "None")
    {
        var prefix = schema.StartsWith("docs_load_", StringComparison.Ordinal) ? "docs_load_" : "docs_crash_";
        Check(schema.StartsWith(prefix, StringComparison.Ordinal) && schema.Length == prefix.Length + 32 &&
            schema[prefix.Length..].All(char.IsAsciiHexDigit), "exact generated owned schema cleanup");
        Check(faultMode is "None" or "Once" or "Three", "bounded schema cleanup fault mode");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (faultMode == "Three" || faultMode == "Once" && attempt == 1)
                {
                    await using var connection = await observer.OpenConnectionAsync();
                    await using var fault = new BlueTuskCommand("SELECT pg_sleep(2)", connection) { CommandTimeout = 1 };
                    _ = await fault.ExecuteScalarAsync();
                    throw new InvalidOperationException("Owned cleanup timeout fault injection did not time out.");
                }
                await using (var command = observer.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"))
                { command.CommandTimeout = 60; _ = await command.ExecuteNonQueryAsync(); }
                await using (var verify = observer.CreateCommand("SELECT count(*) FROM pg_namespace WHERE nspname=@schema"))
                {
                    verify.CommandTimeout = 5;
                    verify.Parameters.Add(new BlueTuskParameter<string>(schema) { ParameterName = "schema" });
                    Check((long)(await verify.ExecuteScalarAsync())! == 0, "owned schema absent after cleanup");
                }
                if (attempt > 1) { Console.WriteLine($"Owned generated schema cleanup succeeded on bounded attempt {attempt}."); }
                return;
            }
            catch (TimeoutException) when (attempt < 3)
            { await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)); }
        }
    }

    private static async Task<CleanupDiagnostics> CaptureCleanupDiagnosticsAsync(BlueTuskDataSource observer, string schema)
    {
        try
        {
            await using var connection = await observer.OpenConnectionAsync();
            await using var exists = new BlueTuskCommand("SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname=@schema)", connection) { CommandTimeout = 5 };
            exists.Parameters.Add(new BlueTuskParameter<string>(schema) { ParameterName = "schema" });
            var schemaExists = (bool)(await exists.ExecuteScalarAsync())!;
            var backends = new List<BackendLockObservation>();
            await using var activity = new BlueTuskCommand("""
                SELECT pid,backend_type,COALESCE(state,''),COALESCE(wait_event_type,''),COALESCE(wait_event,''),
                  cardinality(pg_blocking_pids(pid))
                FROM pg_stat_activity WHERE datname=current_database() ORDER BY pid LIMIT 48
                """, connection) { CommandTimeout = 5 };
            await using (var reader = await activity.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    Check(backends.Count < 48, "bounded cleanup backend snapshot");
                    backends.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5)));
                }
            }
            await using var locks = new BlueTuskCommand("""
                SELECT count(*) FILTER(WHERE NOT l.granted),count(*) FILTER(WHERE l.granted)
                FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.datname=current_database()
                """, connection) { CommandTimeout = 5 };
            await using var values = await locks.ExecuteReaderAsync();
            Check(await values.ReadAsync(), "owned cleanup lock snapshot");
            return new(schema, schemaExists, "captured", values.GetInt64(0), values.GetInt64(1), backends.ToArray());
        }
        catch (Exception exception)
        { return new(schema, null, "unavailable-" + exception.GetType().Name, 0, 0, []); }
    }
}

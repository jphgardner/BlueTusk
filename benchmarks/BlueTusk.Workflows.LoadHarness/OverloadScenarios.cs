using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows.LoadHarness;

internal static partial class Program
{
    private const int OverloadTenants = 4;
    private const int OverloadAdmissionPerTenant = 32;
    private const int OverloadMaximumAccepted = 20000;

    private static async Task<OverloadResult> RunOverloadAsync(string connectionString, int seconds, bool workflow,
        bool physicalStorage = false, string? sampleOutputPrefix = null, string payloadMode = "Repeated")
    {
        int maximumAccepted = physicalStorage ? 200000 : OverloadMaximumAccepted;
        int automaticVacuumObservationSeconds = Math.Min(120, Math.Max(1, seconds / 3));
        int manualVacuumIntervalSeconds = Math.Min(30, Math.Max(1, seconds / 5));
        var compactJson = new HarnessJsonContext(new JsonSerializerOptions { WriteIndented = false });
        string product = workflow ? "Workflows" : "Jobs";
        string? samplePath = physicalStorage && sampleOutputPrefix is not null ? sampleOutputPrefix + "." + product + ".samples.jsonl" : null;
        if (samplePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(samplePath)!);
            await File.WriteAllTextAsync(samplePath, string.Empty);
        }
        Console.WriteLine($"Running {product} offered overload with retention for {seconds} seconds.");
        string suffix = Guid.NewGuid().ToString("N");
        string jobsSchema = "over_jobs_" + suffix;
        string workflowSchema = "over_wf_" + suffix;
        await using var source = Source(connectionString, 32);
        var workflows = new PostgreSqlWorkflowStore(source, new WorkflowOptions { Schema = workflowSchema }, new JobStoreOptions { Schema = jobsSchema });
        var jobs = workflows.Jobs;
        using var stop = new CancellationTokenSource();
        using var producersStop = new CancellationTokenSource();
        SemaphoreSlim[] admissions = Enumerable.Range(0, OverloadTenants).Select(_ => new SemaphoreSlim(OverloadAdmissionPerTenant)).ToArray();
        JobScope[] scopes = Enumerable.Range(0, OverloadTenants).Select(index => new JobScope("tenant-" + index.ToString("D4", CultureInfo.InvariantCulture), "overload")).ToArray();
        var pending = new ConcurrentDictionary<Guid, int>();
        var completionSamples = new ConcurrentBag<(int Tenant, double Milliseconds, double CompletedEpoch)>();
        var storageSamples = new List<StorageSample>();
        var vacuums = new List<VacuumObservation>();
        using var process = Process.GetCurrentProcess();
        double cpuStart = process.TotalProcessorTime.TotalMilliseconds;
        long allocatedStart = GC.GetTotalAllocatedBytes(precise: true);
        int[] collectionsStart = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        int serverMajor = 0;
        double nextVacuum = automaticVacuumObservationSeconds;
        var workerTasks = new List<Task>();
        var producerTasks = new List<Task>();
        Task? collector = null;
        Task? maintenance = null;
        int accepted = 0;
        int reserved = 0;
        int outstanding = 0;
        int maximumOutstanding = 0;
        int prunedPrimary = 0;
        int prunedJobs = 0;
        long[] rejected = new long[OverloadTenants];
        string primaryTable = workflow ? $"\"{workflowSchema}\".instances" : $"\"{jobsSchema}\".jobs";
        string measurementTable = $"\"{workflowSchema}\".completion_measurements";
        var elapsed = new Stopwatch();
        try
        {
            await workflows.InitializeAsync();
            if (physicalStorage)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var command = new BlueTuskCommand("SELECT current_setting('server_version_num')::integer / 10000", connection);
                serverMajor = (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;
            }
            await ExecuteAsync(source, $"""
                CREATE TABLE "{workflowSchema}".effects (id uuid PRIMARY KEY, tenant varchar(200) NOT NULL, sequence integer NOT NULL);
                CREATE TABLE {measurementTable} (id uuid PRIMARY KEY, tenant varchar(200) NOT NULL, created_at timestamptz NOT NULL, completed_at timestamptz NOT NULL);
                CREATE FUNCTION "{workflowSchema}".capture_completion() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    INSERT INTO {measurementTable} VALUES (NEW.id, NEW.tenant, NEW.created_at, NEW.completed_at) ON CONFLICT DO NOTHING;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER load_completion AFTER UPDATE OF status ON {primaryTable}
                    FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status AND NEW.status = 2)
                    EXECUTE FUNCTION "{workflowSchema}".capture_completion();
                """);
            if (workflow)
            {
                foreach (var scope in scopes) { await workflows.RegisterDefinitionAsync(scope, Definition()); }
            }

            var before = await DatabaseProbe.CountersAsync(source, jobsSchema, workflowSchema);
            await using var probe = new DatabaseProbe(source);
            cpuStart = process.TotalProcessorTime.TotalMilliseconds;
            allocatedStart = GC.GetTotalAllocatedBytes(precise: true);
            for (int generation = 0; generation < 3; generation++) { collectionsStart[generation] = GC.CollectionCount(generation); }
            elapsed.Start();
            producersStop.CancelAfter(TimeSpan.FromSeconds(seconds));
            for (int index = 0; index < scopes.Length; index++)
            {
                int tenant = index;
                var options = new JobWorkerOptions
                {
                    Concurrency = 4,
                    ClaimBatchSize = 4,
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    DispatchRecurringSchedules = false,
                    LeaseDuration = TimeSpan.FromSeconds(30),
                    HeartbeatInterval = TimeSpan.FromSeconds(2),
                };
                if (workflow)
                {
                    var registry = new WorkflowActivityRegistry()
                        .Register("produce.v1", HarnessJsonContext.Default.LoadPayload, HarnessJsonContext.Default.LoadPayload, async (payload, context, token) =>
                        {
                            Check(payload.Tenant == tenant, "overload workflow tenant boundary");
                            await Task.Delay(100, token);
                            return payload;
                        })
                        .RegisterTransactional("commit.v1", async (connection, transaction, context, token) =>
                        {
                            var payload = JsonSerializer.Deserialize(context.InitialInput.Span, HarnessJsonContext.Default.LoadPayload)!;
                            Check(payload.Tenant == tenant, "overload workflow effect tenant boundary");
                            await InsertEffectAsync(connection, transaction, workflowSchema, context.Workflow.Id, scopes[tenant].Tenant, payload.Sequence, token);
                            return ReadOnlyMemory<byte>.Empty;
                        });
                    workerTasks.Add(new WorkflowWorker(workflows, scopes[tenant], "overload/" + tenant, registry,
                        new WorkflowWorkerOptions { Jobs = options, RecoveryInterval = TimeSpan.FromSeconds(1) }).RunAsync(stop.Token));
                }
                else
                {
                    var registry = new JobHandlerRegistry().Register("load.v1", HarnessJsonContext.Default.LoadPayload, async (payload, context, token) =>
                    {
                        Check(payload.Tenant == tenant, "overload job tenant boundary");
                        await Task.Delay(100, token);
                        var effect = await jobs.ExecuteFencedAsync(context.Lease!, async (connection, transaction, cancellation) =>
                        {
                            await InsertEffectAsync(connection, transaction, workflowSchema, context.JobId, scopes[tenant].Tenant, payload.Sequence, cancellation);
                            return true;
                        }, token);
                        Check(effect.Executed, "overload live fenced effect");
                    });
                    workerTasks.Add(new JobWorker(jobs, scopes[tenant], "overload/" + tenant, registry, options).RunAsync(stop.Token));
                }

                producerTasks.Add(ProduceAsync(tenant));
            }

            collector = CollectAsync(stop.Token);
            maintenance = MaintainAsync(stop.Token);
            await Task.WhenAll(producerTasks);
            await WaitUntilAsync(async () =>
            {
                if (Volatile.Read(ref outstanding) != 0) { return false; }
                foreach (var scope in scopes)
                {
                    var health = await jobs.InspectAsync(scope, 1);
                    if (health.PendingObserved > 0 || health.RunningObserved > 0) { return false; }
                }

                return true;
            }, TimeSpan.FromSeconds(90));
            elapsed.Stop();
            await stop.CancelAsync();
            await Task.WhenAll(workerTasks);
            await collector;
            await maintenance;
            var runtime = await probe.FinishAsync();
            Check(accepted > 0 && accepted <= maximumAccepted && completionSamples.Count == accepted,
                "overload every accepted item durably completed within finite cap");
            Check(!physicalStorage || accepted < maximumAccepted, "storage admissions ran for full duration without reaching audit cap");
            int effects = await EffectCountAsync(source, workflowSchema);
            Check(effects == accepted && maximumOutstanding <= OverloadAdmissionPerTenant * OverloadTenants,
                "overload idempotent effects and admission memory bound");
            foreach (var scope in scopes)
            {
                if (workflow)
                {
                    while (true)
                    {
                        int count = await workflows.PruneAsync(scope, TimeSpan.Zero, 128);
                        prunedPrimary += count;
                        if (count < 128) { break; }
                    }
                }

                while (true)
                {
                    int count = await jobs.PruneAsync(scope, TimeSpan.Zero, 128);
                    prunedJobs += count;
                    if (!workflow) { prunedPrimary += count; }
                    if (count < 128) { break; }
                }
            }

            storageSamples.Add(await StorageAsync());
            Check(prunedPrimary == accepted && prunedJobs == accepted * (workflow ? 4 : 1), "overload primary and dispatch retention complete");
            Check(storageSamples[^1] is { RetainedPrimaryRows: 0, RetainedJobs: 0, JobAttemptRows: 0 }, "overload runtime rows and attempt history fully pruned");
            await Task.Delay(1100);
            var after = await DatabaseProbe.CountersAsync(source, jobsSchema, workflowSchema);
            var tenants = scopes.Select((scope, tenant) =>
            {
                var samples = completionSamples.Where(row => row.Tenant == tenant).ToArray();
                double[] times = samples.Select(sample => sample.CompletedEpoch).Order().ToArray();
                double gap = times.Length < 2 ? 0 : times.Zip(times.Skip(1), (first, second) => (second - first) * 1000).Max();
                Check(samples.Length > 0, "overload cold and hot tenants make progress");
                return new OverloadTenantResult(scope.Tenant, samples.Length, rejected[tenant], Distribution(samples.Select(sample => sample.Milliseconds)), gap);
            }).ToArray();
            long rejections = rejected.Sum();
            Check(seconds < 5 || rejections > 0, "offered overload produces explicit admission rejections");
            Console.WriteLine($"{product} overload: {accepted} accepted effects, {rejections} rejected offers, maximum outstanding {maximumOutstanding}.");
            return new(product, seconds, maximumAccepted, OverloadAdmissionPerTenant, 4, 100,
                accepted, rejections, effects, maximumOutstanding, prunedPrimary, prunedJobs, elapsed.Elapsed.TotalSeconds,
                accepted / elapsed.Elapsed.TotalSeconds, runtime, before, after, tenants, storageSamples, true,
                physicalStorage ? automaticVacuumObservationSeconds : 0, physicalStorage ? manualVacuumIntervalSeconds : 0,
                vacuums, payloadMode);
        }
        finally
        {
            await producersStop.CancelAsync();
            await stop.CancelAsync();
            await Task.WhenAll(producerTasks.Concat(workerTasks));
            if (collector is not null) { await collector; }
            if (maintenance is not null) { await maintenance; }
            foreach (var admission in admissions) { admission.Dispose(); }
            await ExecuteAsync(source, $"DROP SCHEMA IF EXISTS \"{workflowSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{jobsSchema}\" CASCADE");
        }

        async Task ProduceAsync(int tenant)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tenant == 0 ? 1 : workflow ? 500 : 100));
            int sequence = 0;
            string repeatedPadding = new('x', 1024);
            try
            {
                while (await timer.WaitForNextTickAsync(producersStop.Token))
                {
                    if (!admissions[tenant].Wait(0)) { Interlocked.Increment(ref rejected[tenant]); continue; }
                    bool reserve = false;
                    while (!reserve)
                    {
                        int previous = Volatile.Read(ref reserved);
                        if (previous >= maximumAccepted) { break; }
                        reserve = Interlocked.CompareExchange(ref reserved, previous + 1, previous) == previous;
                    }

                    if (!reserve) { admissions[tenant].Release(); Interlocked.Increment(ref rejected[tenant]); continue; }
                    int current = Interlocked.Increment(ref outstanding);
                    int peak;
                    do { peak = Volatile.Read(ref maximumOutstanding); }
                    while (current > peak && Interlocked.CompareExchange(ref maximumOutstanding, current, peak) != peak);
                    int currentSequence = sequence++;
                    string padding = payloadMode == "SeededHighEntropy"
                        ? SeededPadding(1024, tenant, currentSequence) : repeatedPadding;
                    var payload = new LoadPayload(tenant, currentSequence, Stopwatch.GetTimestamp(), padding);
                    Guid id;
                    if (workflow)
                    {
                        var key = await workflows.StartAsync(new WorkflowStartRequest
                        {
                            Scope = scopes[tenant],
                            Definition = "load",
                            Version = 1,
                            Input = JsonSerializer.SerializeToUtf8Bytes(payload, HarnessJsonContext.Default.LoadPayload),
                        });
                        id = key.Id;
                        Check(await workflows.SignalAsync(key, "continue", "ready", ReadOnlyMemory<byte>.Empty), "overload signal buffered");
                    }
                    else
                    {
                        id = await jobs.EnqueueAsync(JobRequest.FromJson(scopes[tenant], "load.v1", payload, HarnessJsonContext.Default.LoadPayload));
                    }

                    Check(pending.TryAdd(id, tenant), "unique overload admitted identity");
                    Interlocked.Increment(ref accepted);
                }
            }
            catch (OperationCanceledException) when (producersStop.IsCancellationRequested) { }
        }

        async Task CollectAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    foreach (var group in pending.ToArray().GroupBy(pair => pair.Value))
                    {
                        var ids = group.Select(pair => pair.Key).ToArray();
                        await using var connection = await source.OpenConnectionAsync(token);
                        await using var command = new BlueTuskCommand($"SELECT id, (extract(epoch FROM (completed_at - created_at))*1000)::double precision, extract(epoch FROM completed_at)::double precision FROM {measurementTable} WHERE tenant = @tenant AND id IN ({string.Join(',', ids.Select((_, index) => "@id" + index))})", connection);
                        command.Parameters.Add(new BlueTuskParameter<string>(scopes[group.Key].Tenant) { ParameterName = "tenant" });
                        for (int index = 0; index < ids.Length; index++) { command.Parameters.Add(new BlueTuskParameter<Guid>(ids[index]) { ParameterName = "id" + index }); }
                        await using var reader = await command.ExecuteReaderAsync(token);
                        while (await reader.ReadAsync(token))
                        {
                            if (pending.TryRemove(reader.GetGuid(0), out int tenant))
                            {
                                completionSamples.Add((tenant, reader.GetDouble(1), reader.GetDouble(2)));
                                Interlocked.Decrement(ref outstanding);
                                admissions[tenant].Release();
                            }
                        }
                    }

                    await Task.Delay(20, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        async Task MaintainAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    foreach (var scope in scopes)
                    {
                        // Finish an admitted pruning command before shutdown so its acknowledged row count is retained.
                        if (workflow) { prunedPrimary += await workflows.PruneAsync(scope, TimeSpan.FromSeconds(2), 128, CancellationToken.None); }
                        int count = await jobs.PruneAsync(scope, TimeSpan.FromSeconds(2), 128, CancellationToken.None);
                        prunedJobs += count;
                        if (!workflow) { prunedPrimary += count; }
                    }

                    Check(storageSamples.Count < 4096, "bounded retention storage samples");
                    if (physicalStorage && elapsed.Elapsed.TotalSeconds >= nextVacuum)
                    {
                        await VacuumRuntimeAsync();
                        nextVacuum = elapsed.Elapsed.TotalSeconds + manualVacuumIntervalSeconds;
                    }
                    storageSamples.Add(await StorageAsync(CancellationToken.None));
                    if (samplePath is not null)
                    {
                        // Retain raw progress even if a later campaign invariant fails.
                        await File.AppendAllTextAsync(samplePath, JsonSerializer.Serialize(storageSamples[^1], compactJson.StorageSample) + Environment.NewLine, CancellationToken.None);
                    }
                    if (storageSamples.Count % 30 == 0) { Console.WriteLine($"{product} overload elapsed {elapsed.Elapsed.TotalSeconds:F0}s, accepted {Volatile.Read(ref accepted)}, outstanding {Volatile.Read(ref outstanding)}."); }
                    await Task.Delay(1000, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        async Task<StorageSample> StorageAsync(CancellationToken token = default)
        {
            PhysicalStorageObservation? physical = physicalStorage
                ? await PhysicalStorageAsync(source, jobsSchema, workflowSchema, serverMajor, process, cpuStart, allocatedStart, collectionsStart, token)
                : null;
            await using var connection = await source.OpenConnectionAsync(token);
            await using var command = new BlueTuskCommand($"""
                SELECT (SELECT count(*)::integer FROM {primaryTable}),
                    (SELECT count(*)::integer FROM {primaryTable} WHERE status IN (0,1)),
                    (SELECT count(*)::integer FROM "{jobsSchema}".jobs),
                    (SELECT count(*)::integer FROM "{jobsSchema}".job_attempts),
                    (SELECT coalesce(sum(pg_total_relation_size(c.oid)),0)::bigint FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                     WHERE n.nspname IN (@jobs,@workflows) AND c.relkind='r' AND c.relname NOT IN ('effects','completion_measurements'))
                """, connection);
            command.Parameters.Add(new BlueTuskParameter<string>(jobsSchema) { ParameterName = "jobs" });
            command.Parameters.Add(new BlueTuskParameter<string>(workflowSchema) { ParameterName = "workflows" });
            await using var reader = await command.ExecuteReaderAsync(token);
            _ = await reader.ReadAsync(token);
            return new(elapsed.Elapsed.TotalSeconds, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4), physical);
        }

        async Task VacuumRuntimeAsync()
        {
            // Ordinary vacuum reuses dead pages without requesting truncation's exclusive table lock.
            // Finish each admitted maintenance statement before shutdown; all commands retain a finite deadline.
            await using var vacuumSource = Source(connectionString, 1);
            await using var connection = await vacuumSource.OpenConnectionAsync();
            await using (var settings = new BlueTuskCommand("SET lock_timeout = '2s'; SET statement_timeout = '30s'", connection))
            {
                _ = await settings.ExecuteNonQueryAsync(CancellationToken.None);
            }
            var relations = await RuntimeRelationsAsync(connection, jobsSchema, workflowSchema, CancellationToken.None);
            foreach (var relation in relations)
            {
                var duration = Stopwatch.StartNew();
                string schema = relation.Schema.Replace("\"", "\"\"", StringComparison.Ordinal);
                string table = relation.Table.Replace("\"", "\"\"", StringComparison.Ordinal);
                await using var command = new BlueTuskCommand($"VACUUM (ANALYZE, TRUNCATE FALSE, INDEX_CLEANUP ON) \"{schema}\".\"{table}\"", connection) { CommandTimeout = 35 };
                _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
                Check(vacuums.Count < 4096, "bounded vacuum observations");
                vacuums.Add(new(elapsed.Elapsed.TotalSeconds, relation.Schema, relation.Table, duration.Elapsed.TotalMilliseconds));
            }
        }
    }

    private static string SeededPadding(int length, int tenant, int sequence)
    {
        // Distinct deterministic bytes per offered item, then base64 so JSON escaping
        // cannot turn the intended low-compressibility content into an artifact.
        byte[] bytes = new byte[(length * 3 + 3) / 4];
        new Random(unchecked(17041 + tenant * 1_000_003 + sequence)).NextBytes(bytes);
        return Convert.ToBase64String(bytes)[..length];
    }
}

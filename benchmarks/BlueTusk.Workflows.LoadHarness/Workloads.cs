using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using BlueTusk.Data;
using BlueTusk.Jobs;
using BlueTusk.Workflows;

namespace BlueTusk.Workflows.LoadHarness;

internal static partial class Program
{
    private static async Task<CaseResult> RunCaseAsync(string connectionString, LoadCase configuration)
    {
        Check(configuration.Count is > 0 and <= 200000 && configuration.Tenants is > 0 and <= 64 &&
            configuration.ConcurrencyPerTenant is > 0 and <= 64 && configuration.PaddingBytes is >= 0 and <= 65536 &&
            configuration.AdmissionWindow is > 0 and <= 1024, "bounded case configuration");
        string suffix = Guid.NewGuid().ToString("N");
        string jobsSchema = "load_jobs_" + suffix;
        string workflowSchema = "load_wf_" + suffix;
        await using var source = Source(connectionString, configuration.PoolSize);
        var jobOptions = new JobStoreOptions { Schema = jobsSchema };
        var workflowOptions = new WorkflowOptions
        {
            Schema = workflowSchema,
            MaximumResultBytes = Math.Max(65536, configuration.PaddingBytes + 256),
        };
        var workflows = new PostgreSqlWorkflowStore(source, workflowOptions, jobOptions);
        var jobs = workflows.Jobs;
        try
        {
            await workflows.InitializeAsync();
            await ExecuteAsync(source, $"CREATE TABLE \"{workflowSchema}\".effects (id uuid PRIMARY KEY, tenant varchar(200) NOT NULL, sequence integer NOT NULL)");
            if (configuration.RetainedRows > 0)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var command = new BlueTuskCommand($"""
                    INSERT INTO "{jobsSchema}".jobs (tenant, queue, id, job_type, payload, available_at, maximum_attempts, status, completed_at)
                    SELECT 'tenant-' || lpad((g % @tenants)::text, 4, '0'), 'load', gen_random_uuid(), 'retained', ''::bytea,
                        clock_timestamp(), 1, 2, clock_timestamp() FROM generate_series(1, @retained) g
                    """, connection);
                command.Parameters.Add(new BlueTuskParameter<int>(configuration.Tenants) { ParameterName = "tenants" });
                command.Parameters.Add(new BlueTuskParameter<int>(configuration.RetainedRows) { ParameterName = "retained" });
                _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
            }

            JobScope[] scopes = Enumerable.Range(0, configuration.Tenants)
                .Select(index => new JobScope("tenant-" + index.ToString("D4", CultureInfo.InvariantCulture), "load")).ToArray();
            if (configuration.Product == "Workflows")
            {
                foreach (var scope in scopes)
                {
                    await workflows.RegisterDefinitionAsync(scope, Definition());
                }
            }

            var before = await DatabaseProbe.CountersAsync(source, jobsSchema, workflowSchema);
            var handlerDurations = new ConcurrentBag<double>();
            using var workersStop = new CancellationTokenSource();
            using var collectorStop = new CancellationTokenSource();
            using var admission = new SemaphoreSlim(configuration.AdmissionWindow, configuration.AdmissionWindow);
            var pending = new ConcurrentDictionary<Guid, int>();
            var workerTasks = new List<Task>();
            Task? collector = null;
            string padding = new('x', configuration.PaddingBytes);
            int produced = 0;
            int actualPayloadBytes = JobRequest.FromJson(scopes[0], "load.v1", new LoadPayload(0, 0, 0, padding), HarnessJsonContext.Default.LoadPayload).Payload.Length;
            var keys = new ConcurrentBag<WorkflowKey>();
            await using var probe = new DatabaseProbe(source);
            var enqueueTime = Stopwatch.StartNew();
            var drainTime = new Stopwatch();
            try
            {
                void StartWorkers()
                {
                    foreach (var scope in scopes)
                    {
                        var workerOptions = new JobWorkerOptions
                        {
                            Concurrency = configuration.ConcurrencyPerTenant,
                            ClaimBatchSize = Math.Min(32, configuration.ConcurrencyPerTenant),
                            PollInterval = TimeSpan.FromMilliseconds(10),
                            DispatchRecurringSchedules = false,
                            LeaseDuration = TimeSpan.FromSeconds(30),
                            HeartbeatInterval = TimeSpan.FromSeconds(2),
                        };
                        if (configuration.Product == "Jobs")
                        {
                            var registry = new JobHandlerRegistry().Register("load.v1", HarnessJsonContext.Default.LoadPayload, async (payload, context, token) =>
                            {
                                long started = Stopwatch.GetTimestamp();
                                if (configuration.HandlerDelayMilliseconds > 0)
                                {
                                    await Task.Delay(configuration.HandlerDelayMilliseconds, token);
                                }

                                Check(payload.Tenant == Array.IndexOf(scopes, scope), "tenant delivery boundary");
                                var effect = await jobs.ExecuteFencedAsync(context.Lease!, async (connection, transaction, cancellation) =>
                                {
                                    await InsertEffectAsync(connection, transaction, workflowSchema, context.JobId, context.Scope.Tenant, payload.Sequence, cancellation);
                                    return true;
                                }, token);
                                Check(effect.Executed, "fenced effect committed");
                                handlerDurations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                            });
                            workerTasks.Add(new JobWorker(jobs, scope, "load/" + scope.Tenant, registry, workerOptions).RunAsync(workersStop.Token));
                        }
                        else
                        {
                            var registry = new WorkflowActivityRegistry()
                                .Register("produce.v1", HarnessJsonContext.Default.LoadPayload, HarnessJsonContext.Default.LoadPayload,
                                    async (payload, context, token) =>
                                    {
                                        long started = Stopwatch.GetTimestamp();
                                        Check(payload.Tenant == Array.IndexOf(scopes, scope), "workflow tenant boundary");
                                        if (configuration.HandlerDelayMilliseconds > 0)
                                        {
                                            await Task.Delay(configuration.HandlerDelayMilliseconds, token);
                                        }

                                        handlerDurations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                                        return payload;
                                    })
                                .RegisterTransactional("commit.v1", async (connection, transaction, context, token) =>
                                {
                                    long started = Stopwatch.GetTimestamp();
                                    var input = System.Text.Json.JsonSerializer.Deserialize(context.InitialInput.Span, HarnessJsonContext.Default.LoadPayload)!;
                                    Check(input.Tenant == Array.IndexOf(scopes, scope), "transactional workflow tenant boundary");
                                    await InsertEffectAsync(connection, transaction, workflowSchema, context.Workflow.Id, scope.Tenant, input.Sequence, token);
                                    handlerDurations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                                    return ReadOnlyMemory<byte>.Empty;
                                });
                            workerTasks.Add(new WorkflowWorker(workflows, scope, "load/" + scope.Tenant, registry,
                                new WorkflowWorkerOptions { Jobs = workerOptions, RecoveryInterval = TimeSpan.FromSeconds(1) }).RunAsync(workersStop.Token));
                        }
                    }
                }

                async Task ProduceOneAsync(int sequence)
                {
                    int cycle = configuration.HotTenantWeight + configuration.Tenants - 1;
                    int position = sequence % cycle;
                    int tenant = position < configuration.HotTenantWeight ? 0 : position - configuration.HotTenantWeight + 1;
                    var payload = new LoadPayload(tenant, sequence, Stopwatch.GetTimestamp(), padding);
                    if (configuration.Product == "Jobs")
                    {
                        Guid id = await jobs.EnqueueAsync(JobRequest.FromJson(scopes[tenant], "load.v1", payload, HarnessJsonContext.Default.LoadPayload));
                        if (configuration.DurationSeconds > 0)
                        {
                            Check(pending.TryAdd(id, tenant), "unique admitted job");
                        }
                    }
                    else
                    {
                        var request = new WorkflowStartRequest
                        {
                            Scope = scopes[tenant],
                            Definition = "load",
                            Version = 1,
                            Input = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload, HarnessJsonContext.Default.LoadPayload),
                        };
                        var key = await workflows.StartAsync(request);
                        keys.Add(key);
                        Check(await workflows.SignalAsync(key, "continue", "signal-" + sequence, ReadOnlyMemory<byte>.Empty), "buffered workflow signal");
                    }

                    Interlocked.Increment(ref produced);
                }

                if (configuration.DurationSeconds > 0)
                {
                    Check(configuration.Product == "Jobs", "endurance profile product");
                    StartWorkers();
                    drainTime.Start();
                    collector = CollectAdmissionAsync(source, jobsSchema, scopes, pending, admission, collectorStop.Token);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(configuration.DurationSeconds));
                    int sequence = 0;
                    while (sequence < configuration.Count)
                    {
                        try
                        {
                            await admission.WaitAsync(deadline.Token);
                        }
                        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                        {
                            break;
                        }

                        if (deadline.IsCancellationRequested)
                        {
                            admission.Release();
                            break;
                        }

                        await ProduceOneAsync(sequence++);
                    }

                    enqueueTime.Stop();
                }
                else
                {
                    await Parallel.ForEachAsync(Enumerable.Range(0, configuration.Count), new ParallelOptions { MaxDegreeOfParallelism = 4 },
                        async (sequence, _) => await ProduceOneAsync(sequence));
                    enqueueTime.Stop();
                    StartWorkers();
                    drainTime.Start();
                }

                string table = configuration.Product == "Jobs" ? $"\"{jobsSchema}\".jobs" : $"\"{workflowSchema}\".instances";
                string filter = configuration.Product == "Jobs" ? " AND job_type = 'load.v1'" : string.Empty;
                await WaitUntilAsync(async () =>
                {
                    bool complete;
                    await using (var connection = await source.OpenConnectionAsync())
                    await using (var command = new BlueTuskCommand($"SELECT count(*) FILTER (WHERE status = 2), count(*) FILTER (WHERE status IN (3,4)) FROM {table} WHERE true {filter}", connection))
                    await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
                    {
                        _ = await reader.ReadAsync(CancellationToken.None);
                        Check(reader.GetInt64(1) == 0, "no terminal workload failures");
                        complete = reader.GetInt64(0) == produced;
                    }

                    if (!complete) { return false; }
                    // Workflow result commit can precede the Jobs acknowledgement. Keep workers alive until both drain.
                    foreach (var scope in scopes)
                    {
                        var health = await jobs.InspectAsync(scope, 1);
                        if (health.PendingObserved > 0 || health.RunningObserved > 0) { return false; }
                    }

                    return true;
                }, TimeSpan.FromSeconds(Math.Max(90, configuration.DurationSeconds + 60)));
                drainTime.Stop();
            }
            finally
            {
                await workersStop.CancelAsync();
                await collectorStop.CancelAsync();
                await Task.WhenAll(workerTasks);
                if (collector is not null)
                {
                    await collector;
                }
            }

            var runtime = await probe.FinishAsync();
            await Task.Delay(1100); // PostgreSQL stats flush asynchronously; this does not make shared counters exclusive.
            var after = await DatabaseProbe.CountersAsync(source, jobsSchema, workflowSchema);
            var durable = await ReadDurableLatenciesAsync(source, jobsSchema, workflowSchema, configuration.Product);
            Check(durable.Count == produced, "all durable completion rows retained for measurement");
            int effects;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var command = new BlueTuskCommand($"SELECT count(*)::integer FROM \"{workflowSchema}\".effects", connection))
            {
                effects = (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;
            }

            Check(effects == produced, "one durable business effect per completion");
            if (configuration.Product == "Workflows")
            {
                foreach (var key in keys)
                {
                    Check((await workflows.ReplayAsync(key))!.MatchesPersistedState, "every workflow history replays");
                }
            }

            int pruned = 0;
            foreach (var scope in scopes)
            {
                var jobHealth = await jobs.InspectAsync(scope, 1);
                Check(jobHealth.PendingObserved == 0 && jobHealth.RunningObserved == 0, "Jobs health confirms complete drain");
                var workflowHealth = await workflows.InspectAsync(scope, 1);
                Check(workflowHealth.RunningObserved == 0 && workflowHealth.CompensatingObserved == 0, "Workflows health confirms complete drain");
                while (true)
                {
                    int count = configuration.Product == "Jobs" ? await jobs.PruneAsync(scope, TimeSpan.Zero, 128)
                        : await workflows.PruneAsync(scope, TimeSpan.Zero, 128);
                    pruned += count;
                    if (count < 128)
                    {
                        break;
                    }
                }
            }

            Check(pruned == produced + (configuration.Product == "Jobs" ? configuration.RetainedRows : 0), "bounded retention removes every terminal row");

            var tenants = durable.GroupBy(row => row.Tenant, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new TenantResult(group.Key, group.Count(), Distribution(group.Select(row => row.Milliseconds)))).ToArray();
            return new(configuration, produced, durable.Count, effects, actualPayloadBytes, enqueueTime.Elapsed.TotalSeconds,
                drainTime.Elapsed.TotalSeconds, produced / drainTime.Elapsed.TotalSeconds,
                Distribution(durable.Select(row => row.Milliseconds)), Distribution(handlerDurations), tenants,
                runtime, before, after, after.WalPosition >= before.WalPosition ? after.WalPosition - before.WalPosition : 0,
                after.RelationBytes - before.RelationBytes, pruned, Verified: true);
        }
        finally
        {
            await ExecuteAsync(source, $"DROP SCHEMA IF EXISTS \"{workflowSchema}\" CASCADE");
            await ExecuteAsync(source, $"DROP SCHEMA IF EXISTS \"{jobsSchema}\" CASCADE");
        }
    }

    private static WorkflowDefinition Definition() => new()
    {
        Name = "load",
        Version = 1,
        Nodes =
        [
            new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "produce.v1" },
            new() { Id = "b", Kind = WorkflowNodeKind.Activity, Activity = "produce.v1" },
            new() { Id = "join", Kind = WorkflowNodeKind.Join, DependsOn = ["a", "b"] },
            new() { Id = "signal", Kind = WorkflowNodeKind.Signal, Signal = "continue", DependsOn = ["join"] },
            new() { Id = "timer", Kind = WorkflowNodeKind.Timer, Delay = TimeSpan.FromMilliseconds(10), DependsOn = ["signal"] },
            new() { Id = "commit", Kind = WorkflowNodeKind.Activity, Activity = "commit.v1", DependsOn = ["timer"] },
        ],
    };

    private static async ValueTask InsertEffectAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        string schema, Guid id, string tenant, int sequence, CancellationToken cancellationToken)
    {
        await using var command = new BlueTuskCommand($"INSERT INTO \"{schema}\".effects VALUES (@id, @tenant, @sequence) ON CONFLICT DO NOTHING", connection) { Transaction = transaction };
        command.Parameters.Add(new BlueTuskParameter<Guid>(id) { ParameterName = "id" });
        command.Parameters.Add(new BlueTuskParameter<string>(tenant) { ParameterName = "tenant" });
        command.Parameters.Add(new BlueTuskParameter<int>(sequence) { ParameterName = "sequence" });
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<(string Tenant, double Milliseconds)>> ReadDurableLatenciesAsync(BlueTuskDataSource source,
        string jobsSchema, string workflowSchema, string product)
    {
        string table = product == "Jobs" ? $"\"{jobsSchema}\".jobs" : $"\"{workflowSchema}\".instances";
        string filter = product == "Jobs" ? " AND job_type = 'load.v1'" : string.Empty;
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new BlueTuskCommand($"SELECT tenant, (extract(epoch FROM (completed_at - created_at)) * 1000)::double precision FROM {table} WHERE status = 2 {filter} LIMIT 200001", connection);
        var latencies = new List<(string Tenant, double Milliseconds)>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            double milliseconds = reader.GetDouble(1);
            Check(milliseconds >= 0, "nonnegative database clock latency");
            latencies.Add((reader.GetString(0), milliseconds));
        }

        Check(latencies.Count <= 200000, "latency sample admission bound");
        return latencies;
    }

    private static async Task CollectAdmissionAsync(BlueTuskDataSource source, string schema, JobScope[] scopes,
        ConcurrentDictionary<Guid, int> pending, SemaphoreSlim admission, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var group in pending.ToArray().GroupBy(pair => pair.Value))
                {
                    var ids = group.Select(pair => pair.Key).ToArray();
                    string filter = string.Join(',', ids.Select((_, index) => "@id" + index));
                    await using var connection = await source.OpenConnectionAsync(cancellationToken);
                    await using var command = new BlueTuskCommand($"SELECT id FROM \"{schema}\".jobs WHERE tenant = @tenant AND queue = 'load' AND status = 2 AND id IN ({filter})", connection);
                    command.Parameters.Add(new BlueTuskParameter<string>(scopes[group.Key].Tenant) { ParameterName = "tenant" });
                    for (int index = 0; index < ids.Length; index++)
                    {
                        command.Parameters.Add(new BlueTuskParameter<Guid>(ids[index]) { ParameterName = "id" + index });
                    }

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (pending.TryRemove(reader.GetGuid(0), out _))
                        {
                            admission.Release();
                        }
                    }
                }

                await Task.Delay(20, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Admission collector is observed when the scenario ends.
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Jobs;
using BlueTusk.Jobs.DependencyInjection;
using BlueTusk.Workflows.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using static BlueTusk.Workflows.PhysicalRecoveryTests.PhysicalRecoveryFixture;

namespace BlueTusk.Workflows.PhysicalRecoveryTests;

public sealed class JobsWorkflowPhysicalRecoveryTests
{
    private static readonly string[] Tenants = ["physical-a", "physical-b"];
    private static readonly string[] Modes = ["signal", "replay", "cancel"];
    [Fact]
    public async Task SynchronousPhysicalPromotionPreservesAcknowledgedWorkAndRecoversFencedEffectsTimersAndCompensation()
    {
        var fixture = new PhysicalRecoveryFixture(); // Missing configuration is an explicit failure, never a skip.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        CancellationToken token = deadline.Token;
        await using var primary = BlueTuskDataSource.Create(fixture.PrimaryConnection);
        await using var standby = BlueTuskDataSource.Create(fixture.StandbyConnection);
        await using var routed = fixture.CreateMultihostSource();
        string suffix = Guid.NewGuid().ToString("N");
        var options = new WorkflowOptions { Schema = "physical_wf_" + suffix };
        var jobOptions = new JobStoreOptions { Schema = "physical_jobs_" + suffix };
        var store = new PostgreSqlWorkflowStore(routed, options, jobOptions);
        string business = "\"" + options.Schema + "\"";
        long started = Stopwatch.GetTimestamp();
        var measurements = new List<PhaseMeasurement>();
        var jobs = new List<(JobRequest Request, Guid Id)>();
        var workflows = new List<(WorkflowStartRequest Request, WorkflowKey Key, string Mode)>();
        var jobEntered = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        var workflowEntered = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        var recoveredEntered = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        var calls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var operationSamples = new ConcurrentDictionary<(string Phase, string Name), ConcurrentQueue<double>>();
        var workers = new List<Task>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? probe = null;
        var samples = new ConcurrentQueue<PhaseMeasurement>();
        int maxPoolTotal = 0;
        int maxPoolBusy = 0;
        int maxPoolWaiting = 0;
        int sampleCount = 0;
        bool promoted = false;
        var resumeAfterPromotion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRecoveredAcknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long firstReplayedEffect = 0;
        try
        {
            Assert.Equal("remote_apply", await ScalarAsync<string>(primary, "SHOW synchronous_commit", token));
            Assert.Equal(1L, await ScalarAsync<long>(primary, "SELECT count(*) FROM pg_stat_replication WHERE state='streaming' AND sync_state='sync'", token));
            Assert.True(await ScalarAsync<bool>(standby, "SELECT pg_is_in_recovery()", token));
            string beforeSystem = await ScalarAsync<string>(primary, "SELECT system_identifier::text FROM pg_control_system()", token);
            int beforeTimeline = await ScalarAsync<int>(primary, "SELECT timeline_id FROM pg_control_checkpoint()", token);
            await store.InitializeAsync(token);
            await ExecuteAsync(routed, $"CREATE TABLE {business}.admissions (tenant text NOT NULL, identity uuid NOT NULL, PRIMARY KEY(tenant,identity)); CREATE TABLE {business}.effects (tenant text NOT NULL, product text NOT NULL, identity uuid NOT NULL, phase text NOT NULL, idempotency text NOT NULL, PRIMARY KEY(tenant,product,identity,phase))", token);
            await MeasureAsync("initialized", primary);

            // Process-side sampling is bounded independently of database availability.
            probe = Task.Run(async () =>
            {
                using var process = Process.GetCurrentProcess();
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                try
                {
                    while (await timer.WaitForNextTickAsync(lifetime.Token))
                    {
                        process.Refresh();
                        var pool = routed.GetPoolStatistics();
                        maxPoolTotal = Math.Max(maxPoolTotal, pool.Total);
                        maxPoolBusy = Math.Max(maxPoolBusy, pool.Busy);
                        maxPoolWaiting = Math.Max(maxPoolWaiting, pool.Waiting);
                        Assert.InRange(pool.Total, 0, 16);
                        if (Interlocked.Increment(ref sampleCount) % 10 == 0 && samples.Count < 180)
                        {
                            samples.Enqueue(new("process-sample", Stopwatch.GetElapsedTime(started).TotalMilliseconds, "", 0,
                                process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64, GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes(),
                                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), pool.Total, pool.Busy, pool.Waiting));
                        }
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }, CancellationToken.None);

            var jobRegistry = new JobHandlerRegistry().Register("physical.v1", PhysicalRecoveryJsonContext.Default.PhysicalPayload, async (payload, context, cancellation) =>
            {
                Assert.Equal(payload.Tenant, context.Scope.Tenant);
                if (payload.Mode != "hold") { await resumeAfterPromotion.Task.WaitAsync(cancellation); }
                var effect = await TimedAsync("job.fenced_effect", () => store.Jobs.ExecuteFencedAsync(context.Lease!, async (connection, transaction, transactionToken) =>
                {
                    return await EffectAsync(connection, transaction, payload.Tenant, "Jobs", context.JobId, "done", context.JobId.ToString("N"), transactionToken);
                }, cancellation));
                Assert.True(effect.Executed);
                if (payload.Mode == "hold" && context.Attempt == 1)
                {
                    jobEntered[payload.Tenant].TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                }
                else if (payload.Mode == "hold")
                {
                    Assert.Equal(2, context.Attempt);
                    _ = Interlocked.CompareExchange(ref firstReplayedEffect, Stopwatch.GetTimestamp(), 0);
                    recoveredEntered[payload.Tenant].TrySetResult();
                    await allowRecoveredAcknowledgement.Task.WaitAsync(cancellation);
                }
            });
            var registry = new WorkflowActivityRegistry()
                .RegisterTransactional("produce", async (connection, transaction, context, cancellation) =>
                {
                    var payload = Decode(context);
                    Increment(context, "produce");
                    _ = await EffectAsync(connection, transaction, payload.Tenant, "Workflows", context.Workflow.Id, "produce", context.IdempotencyKey, cancellation);
                    return new byte[] { 42 };
                })
                .Register("replay", async (context, cancellation) =>
                {
                    var payload = Decode(context);
                    Increment(context, "produce");
                    await StandaloneEffectAsync(context, payload, "produce", cancellation);
                    if (context.Attempt == 1)
                    {
                        workflowEntered[context.Workflow.Id.ToString("N")].TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                    }
                    else
                    {
                        Assert.Equal(2, context.Attempt);
                        _ = Interlocked.CompareExchange(ref firstReplayedEffect, Stopwatch.GetTimestamp(), 0);
                        recoveredEntered[context.Workflow.Id.ToString("N")].TrySetResult();
                        await allowRecoveredAcknowledgement.Task.WaitAsync(cancellation);
                    }
                    return new byte[] { 42 };
                })
                .RegisterTransactional("finish", async (connection, transaction, context, cancellation) =>
                {
                    var payload = Decode(context);
                    Assert.Equal(42, context.DependencyResults["produce"].Span[0]);
                    Increment(context, "finish");
                    _ = await EffectAsync(connection, transaction, payload.Tenant, "Workflows", context.Workflow.Id, "finish", context.IdempotencyKey, cancellation);
                    return ReadOnlyMemory<byte>.Empty;
                })
                .Register("undo", async (context, cancellation) =>
                {
                    var payload = Decode(context);
                    Assert.True(context.IsCompensation);
                    Assert.Equal(42, context.ActivityResult.Span[0]);
                    Increment(context, "undo");
                    await StandaloneEffectAsync(context, payload, "undo", cancellation);
                    if (context.Attempt == 1)
                    {
                        workflowEntered[context.Workflow.Id.ToString("N")].TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                    }
                    else
                    {
                        Assert.Equal(2, context.Attempt);
                        _ = Interlocked.CompareExchange(ref firstReplayedEffect, Stopwatch.GetTimestamp(), 0);
                        recoveredEntered[context.Workflow.Id.ToString("N")].TrySetResult();
                        await allowRecoveredAcknowledgement.Task.WaitAsync(cancellation);
                    }
                    return ReadOnlyMemory<byte>.Empty;
                });
            foreach (string tenant in Tenants)
            {
                var jobScope = new JobScope(tenant, "jobs");
                var workflowScope = new JobScope(tenant, "workflows");
                jobEntered[tenant] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                recoveredEntered[tenant] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var hold = Request(jobScope, "hold", 0);
                jobs.Add((hold, await AdmitAsync(hold)));
                foreach (string mode in Modes)
                {
                    await store.RegisterDefinitionAsync(workflowScope, Definition(mode), token);
                    var request = new WorkflowStartRequest
                    {
                        Scope = workflowScope,
                        Definition = mode,
                        Version = 1,
                        DeduplicationKey = "physical-" + mode,
                        Input = JsonSerializer.SerializeToUtf8Bytes(new PhysicalPayload(tenant, mode, 0), PhysicalRecoveryJsonContext.Default.PhysicalPayload),
                    };
                    var key = await store.StartAsync(request, token);
                    workflowEntered[key.Id.ToString("N")] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (mode != "signal") { recoveredEntered[key.Id.ToString("N")] = new(TaskCreationOptions.RunContinuationsAsynchronously); }
                    workflows.Add((request, key, mode));
                }
                workers.Add(new JobWorker(store.Jobs, jobScope, "physical-jobs-" + tenant, jobRegistry, WorkerOptions(4)).RunAsync(lifetime.Token));
                workers.Add(new WorkflowWorker(store, workflowScope, "physical-wf-" + tenant, registry, new WorkflowWorkerOptions
                {
                    Jobs = WorkerOptions(3),
                    RecoveryInterval = TimeSpan.FromMilliseconds(100),
                    RecoveryBatchSize = 16,
                }).RunAsync(lifetime.Token));
            }
            await Task.WhenAll(jobEntered.Values.Select(value => value.Task)).WaitAsync(token);
            foreach (var workflow in workflows.Where(value => value.Mode is "signal" or "cancel"))
            {
                await WaitAsync(async () => (await store.ReadNodesAsync(workflow.Key, token)).Single(node => node.Id == "produce").Status == WorkflowNodeStatus.Completed, token);
                if (workflow.Mode == "cancel") { Assert.True(await store.CancelAsync(workflow.Key, compensate: true, token)); }
            }
            await Task.WhenAll(workflows.Where(value => value.Mode != "signal").Select(value => workflowEntered[value.Key.Id.ToString("N")].Task)).WaitAsync(token);

            // Capture genuine persisted capabilities before server loss.
            var oldLeases = new List<(string Product, JobLease Lease)>();
            foreach (var job in jobs) { oldLeases.Add(("Jobs", await ReadLeaseAsync(routed, jobOptions.Schema, job.Request.Scope, job.Id, token))); }
            foreach (var workflow in workflows.Where(value => value.Mode != "signal"))
            {
                var node = (await store.ReadNodesAsync(workflow.Key, token)).Single(value => value.Id == "produce");
                Guid id = (workflow.Mode == "cancel" ? node.CompensationJobId : node.JobId)!.Value;
                oldLeases.Add(("Workflows", await ReadLeaseAsync(routed, jobOptions.Schema, workflow.Key.Scope, id, token)));
            }
            var due = new List<DateTimeOffset>();
            foreach (var workflow in workflows.Where(value => value.Mode == "signal"))
            {
                Assert.True(await store.SignalAsync(workflow.Key, "resume", "before-primary-loss", ReadOnlyMemory<byte>.Empty, token));
                var timer = (await store.ReadNodesAsync(workflow.Key, token)).Single(node => node.Id == "timer");
                var dispatch = (await store.Jobs.ReadAsync(workflow.Key.Scope, timer.JobId!.Value, token))!;
                Assert.Equal(JobStatus.Pending, dispatch.Status);
                due.Add(dispatch.AvailableAt);
            }
            foreach (string tenant in Tenants)
            {
                for (int index = 1; index <= 32; index++)
                {
                    var request = Request(new(tenant, "jobs"), index <= 16 ? "pending" : "delayed", index);
                    Guid id = await AdmitAsync(request);
                    jobs.Add((request, id));
                    if (index == 32) { due.Add((await store.Jobs.ReadAsync(request.Scope, id, token))!.AvailableAt); }
                }
            }
            Assert.Equal(66, jobs.Count);
            Assert.Equal(66L, await ScalarAsync<long>(standby, $"SELECT count(*) FROM {business}.admissions", token));
            Assert.Equal(66L, await ScalarAsync<long>(standby, $"SELECT count(*) FROM \"{jobOptions.Schema}\".jobs WHERE queue='jobs'", token));
            Assert.Equal(8L, await ScalarAsync<long>(standby, $"SELECT count(*) FROM {business}.effects WHERE product='Workflows'", token));
            Assert.True(await ScalarAsync<DateTimeOffset>(primary, "SELECT clock_timestamp()", token) < due.Max());
            await MeasureAsync("acknowledged-before-primary-loss", primary);
            long killed = Stopwatch.GetTimestamp();
            await fixture.KillPrimaryAsync(token);
            var outageCheck = new JobQueueHealthCheck(store.Jobs, new("physical-a", "jobs"), new JobHealthCheckOptions { Timeout = TimeSpan.FromMilliseconds(700) });
            var outage = await outageCheck.ReadAsync(token);
            Assert.Equal(HealthStatus.Unhealthy, outage.Status);
            // The read-only standby proves timers/delayed work have become due while writes are unavailable.
            await WaitAsync(async () => await ScalarAsync<DateTimeOffset>(standby, "SELECT clock_timestamp()", token) > due.Max() + TimeSpan.FromMilliseconds(100), token, TimeSpan.FromSeconds(20));
            await MeasureAsync("read-only-outage-past-durable-deadlines", standby);
            await fixture.PromoteAsync(token);
            promoted = true;
            resumeAfterPromotion.TrySetResult();
            Assert.False(await ScalarAsync<bool>(standby, "SELECT pg_is_in_recovery()", token));
            double promotion = Stopwatch.GetElapsedTime(killed).TotalMilliseconds;
            await ExecuteAsync(standby, "ALTER SYSTEM SET synchronous_standby_names=''", token);
            await ExecuteAsync(standby, "ALTER SYSTEM SET synchronous_commit='on'", token);
            await ExecuteAsync(standby, "SELECT pg_reload_conf()", token);
            // No source reconstruction, routing shim, pool clear or replacement worker is used.
            await Task.WhenAll(recoveredEntered.Values.Select(value => value.Task)).WaitAsync(TimeSpan.FromSeconds(45), token);
            double firstEffectReplay = Stopwatch.GetElapsedTime(killed, Volatile.Read(ref firstReplayedEffect)).TotalMilliseconds;
            var leaseProofs = new List<RecoveredLease>();
            foreach (var old in oldLeases)
            {
                var current = (await store.Jobs.ReadAsync(old.Lease.Scope, old.Lease.JobId, token))!;
                Assert.Equal(JobStatus.Running, current.Status);
                Assert.Equal(2, current.Attempts);
                Assert.True(current.FencingToken > old.Lease.FencingToken);
                bool rejected = !await store.Jobs.CompleteAsync(old.Lease, token);
                bool callbackInvoked = false;
                var stale = await store.Jobs.ExecuteFencedAsync(old.Lease, (_, _, _) => { callbackInvoked = true; return ValueTask.FromResult(true); }, token);
                Assert.True(rejected);
                Assert.False(stale.Executed);
                Assert.False(callbackInvoked);
                leaseProofs.Add(new(old.Product, old.Lease.Scope.Tenant, old.Lease.JobId, current.Attempts, old.Lease.FencingToken, current.FencingToken, rejected, !stale.Executed));
            }
            await MeasureAsync("stale-capabilities-rejected-while-new-leases-running", standby);
            allowRecoveredAcknowledgement.TrySetResult();
            await WaitAsync(async () => (await TimedAsync("job.read", () => store.Jobs.ReadAsync(jobs[0].Request.Scope, jobs[0].Id, token)))!.Status == JobStatus.Succeeded, token);
            double firstRecovered = Stopwatch.GetElapsedTime(killed).TotalMilliseconds;
            await WaitAsync(async () =>
            {
                foreach (string tenant in Tenants)
                {
                    var queue = await TimedAsync("job.health", () => store.Jobs.InspectAsync(new(tenant, "jobs"), cancellationToken: token));
                    if (queue.PendingObserved != 0 || queue.RunningObserved != 0) { return false; }
                }
                foreach (var workflow in workflows)
                {
                    if ((await TimedAsync("workflow.read", () => store.ReadAsync(workflow.Key, token)))!.Status != (workflow.Mode == "cancel" ? WorkflowStatus.Canceled : WorkflowStatus.Succeeded)) { return false; }
                }
                foreach (string tenant in Tenants)
                {
                    var health = await TimedAsync("job.health", () => store.Jobs.InspectAsync(new(tenant, "workflows"), cancellationToken: token));
                    if (health.PendingObserved != 0 || health.RunningObserved != 0) { return false; }
                }
                return true;
            }, token);
            double drained = Stopwatch.GetElapsedTime(killed).TotalMilliseconds;
            await MeasureAsync("durable-recovery-drained", standby);

            var workflowProofs = new List<WorkflowProof>();
            foreach (var workflow in workflows)
            {
                Assert.Equal(workflow.Key, await store.StartAsync(workflow.Request, token));
                var replay = (await store.ReplayAsync(workflow.Key, token))!;
                Assert.True(replay.MatchesPersistedState);
                int produce = Count(workflow.Key.Id, "produce");
                int undo = Count(workflow.Key.Id, "undo");
                int finish = Count(workflow.Key.Id, "finish");
                Assert.Equal(workflow.Mode == "replay" ? 2 : 1, produce);
                Assert.Equal(workflow.Mode == "cancel" ? 2 : 0, undo);
                Assert.Equal(workflow.Mode == "cancel" ? 0 : 1, finish);
                var history = await store.ReadHistoryAsync(workflow.Key, 0, 64, token);
                Assert.Single(history, entry => entry.Event == "activity_completed" && entry.NodeId == "produce");
                if (workflow.Mode == "cancel")
                {
                    Assert.Single(history, entry => entry.Event == "compensation_completed");
                    Assert.False(await store.SignalAsync(workflow.Key, "never", "after-cancel", ReadOnlyMemory<byte>.Empty, token));
                }
                else { Assert.Single(history, entry => entry.Event == "timer_fired"); }
                var foreignKey = workflow.Key with { Scope = new(workflow.Key.Scope.Tenant == "physical-a" ? "physical-b" : "physical-a", "workflows") };
                Assert.Null(await store.ReadAsync(foreignKey, token));
                workflowProofs.Add(new(workflow.Key.Scope.Tenant, workflow.Mode, workflow.Key.Id, (await store.ReadAsync(workflow.Key, token))!.Status.ToString(), produce, undo, finish, true));
            }
            await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (job, cancellation) =>
            {
                Assert.Equal(JobStatus.Succeeded, (await TimedAsync("job.read", () => store.Jobs.ReadAsync(job.Request.Scope, job.Id, cancellation)))!.Status);
                Assert.Equal(job.Id, await TimedAsync("job.enqueue_recheck", () => store.Jobs.EnqueueAsync(job.Request, cancellation)));
                Assert.Null(await store.Jobs.ReadAsync(new(job.Request.Scope.Tenant == "physical-a" ? "physical-b" : "physical-a", "jobs"), job.Id, cancellation));
            });
            Assert.Equal(66L, await ScalarAsync<long>(standby, $"SELECT count(*) FROM {business}.admissions", token));
            Assert.Equal(66L, await ScalarAsync<long>(standby, $"SELECT count(*) FROM {business}.effects WHERE product='Jobs'", token));
            Assert.Equal(12L, await ScalarAsync<long>(standby, $"SELECT count(*) FROM {business}.effects WHERE product='Workflows'", token));
            Assert.Equal(0L, await ScalarAsync<long>(standby, $"SELECT count(*) FROM {business}.effects e LEFT JOIN \"{jobOptions.Schema}\".jobs j ON e.tenant=j.tenant AND e.identity=j.id AND j.queue='jobs' WHERE e.product='Jobs' AND j.id IS NULL", token));
            foreach (string tenant in Tenants)
            {
                Assert.Equal(33L, await ScalarAsync<long>(standby, $"SELECT count(*) FROM {business}.effects WHERE product='Jobs' AND tenant='{tenant}'", token));
                Assert.Equal(6L, await ScalarAsync<long>(standby, $"SELECT count(*) FROM {business}.effects WHERE product='Workflows' AND tenant='{tenant}'", token));
                Assert.Equal(HealthStatus.Healthy, (await new JobQueueHealthCheck(store.Jobs, new(tenant, "jobs"), new()).ReadAsync(token)).Status);
                Assert.Equal(HealthStatus.Healthy, (await new WorkflowScopeHealthCheck(store, new(tenant, "workflows"), new()).ReadAsync(token)).Status);
            }
            string afterSystem = await ScalarAsync<string>(standby, "SELECT system_identifier::text FROM pg_control_system()", token);
            Assert.Equal(beforeSystem, afterSystem);
            // The filename reflects the active timeline without requiring an artificial checkpoint.
            string timelineHex = await ScalarAsync<string>(standby, "SELECT substring(pg_walfile_name(pg_current_wal_lsn()) FROM 1 FOR 8)", token);
            int afterTimeline = Convert.ToInt32(timelineHex, 16);
            Assert.True(afterTimeline > beforeTimeline);
            await MeasureAsync("all-invariants-verified", standby);
            await lifetime.CancelAsync();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
            await probe.WaitAsync(TimeSpan.FromSeconds(10));
            measurements.AddRange(samples);
            measurements.Add(new("sampled-pool-high-water", Stopwatch.GetElapsedTime(started).TotalMilliseconds, "", 0, 0, 0, 0, 0, 0, 0, 0, maxPoolTotal, maxPoolBusy, maxPoolWaiting));
            string server = await ScalarAsync<string>(standby, "SELECT version()", token);
            var report = new PhysicalRecoveryReport(fixture.Fixture, server, Required("IMAGE"), "Verified synchronous remote_apply before primary loss; promoted survivor uses local synchronous_commit=on with no replica",
                beforeSystem, afterSystem, beforeTimeline, afterTimeline, jobs.Count, jobs.Count, 66, 66, 12,
                promotion, firstEffectReplay, firstRecovered, drained, true, true, true, true, leaseProofs, workflowProofs, measurements, OperationReport(), RecoveryConfiguration,
                "Owned local PostgreSQL physical-promotion rehearsal. One stopped primary and one promoted synchronous standby; no async-replication, split-brain, rejoin, server-upgrade, power-loss or production availability claim. Resource snapshots are process-cumulative and PostgreSQL statistics can lag.");
            await File.WriteAllTextAsync(fixture.ReportPath, JsonSerializer.Serialize(report, PhysicalRecoveryJsonContext.Default.PhysicalRecoveryReport), token);
        }
        catch (Exception)
        {
            if (promoted)
            {
                using var diagnosticDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    string diagnostic = await ScalarAsync<string>(standby, $"""
                        SELECT json_build_object(
                            'jobs',(SELECT json_agg(r) FROM (SELECT tenant,queue,status,attempts,last_failure_code,count(*),count(*) FILTER(WHERE available_at<=clock_timestamp()) AS due,count(*) FILTER(WHERE status=1 AND lease_expires<=clock_timestamp()) AS expired_leases FROM "{jobOptions.Schema}".jobs GROUP BY 1,2,3,4,5 ORDER BY 1,2,3,4) r),
                            'workflows',(SELECT json_agg(r) FROM (SELECT tenant,definition,status,failure_code FROM {business}.instances ORDER BY 1,2) r),
                            'nodes',(SELECT json_agg(r) FROM (SELECT tenant,id,status,fencing_token,failure_code,count(*) FROM {business}.nodes GROUP BY 1,2,3,4,5 ORDER BY 1,2,3,4) r)
                        )::text
                        """, diagnosticDeadline.Token);
                    await File.WriteAllTextAsync(fixture.ReportPath + ".failure.json", diagnostic, diagnosticDeadline.Token);
                    await File.WriteAllTextAsync(fixture.ReportPath + ".failure.operations.json", JsonSerializer.Serialize(OperationReport(), PhysicalRecoveryJsonContext.Default.OperationMeasurementArray), diagnosticDeadline.Token);
                    await File.WriteAllTextAsync(fixture.ReportPath + ".configuration.txt", RecoveryConfiguration, diagnosticDeadline.Token);
                }
                catch (Exception) { /* Preserve the original assertion; failed diagnostics are not passing evidence. */ }
            }
            throw;
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10)); } finally { if (probe is not null) { await probe.WaitAsync(TimeSpan.FromSeconds(10)); } }
            // Volume/container cleanup is the label-checked wrapper's responsibility, including failed promotion.
            if (promoted)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await ExecuteAsync(standby, $"DROP SCHEMA IF EXISTS {business} CASCADE; DROP SCHEMA IF EXISTS \"{jobOptions.Schema}\" CASCADE", cleanup.Token);
            }
        }

        JobRequest Request(JobScope scope, string mode, int number) => JobRequest.FromJson(scope, "physical.v1", new PhysicalPayload(scope.Tenant, mode, number), PhysicalRecoveryJsonContext.Default.PhysicalPayload) with
        { DeduplicationKey = "physical-" + number, Delay = mode == "delayed" ? TimeSpan.FromSeconds(8) : TimeSpan.Zero };

        async Task<Guid> AdmitAsync(JobRequest request)
        {
            await using var connection = await routed.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            Guid id = await store.Jobs.EnqueueAsync(request, transaction, token);
            await using var command = new BlueTuskCommand($"INSERT INTO {business}.admissions VALUES (@tenant,@id)", connection) { Transaction = transaction };
            Add(command, "tenant", request.Scope.Tenant); Add(command, "id", id);
            _ = await command.ExecuteNonQueryAsync(token);
            await transaction.CommitAsync(token);
            return id;
        }
        PhysicalPayload Decode(WorkflowActivityContext context)
        {
            var payload = JsonSerializer.Deserialize(context.InitialInput.Span, PhysicalRecoveryJsonContext.Default.PhysicalPayload)!;
            Assert.Equal(payload.Tenant, context.Workflow.Scope.Tenant);
            return payload;
        }
        void Increment(WorkflowActivityContext context, string phase) => calls.AddOrUpdate(context.Workflow.Id.ToString("N") + ":" + phase, 1, static (_, count) => count + 1);
        int Count(Guid id, string phase) => calls.GetValueOrDefault(id.ToString("N") + ":" + phase);
        async Task StandaloneEffectAsync(WorkflowActivityContext context, PhysicalPayload payload, string phase, CancellationToken cancellation)
        {
            await using var connection = await routed.OpenConnectionAsync(cancellation);
            await using var transaction = await connection.BeginTransactionAsync(cancellation);
            _ = await EffectAsync(connection, transaction, payload.Tenant, "Workflows", context.Workflow.Id, phase, context.IdempotencyKey, cancellation);
            await transaction.CommitAsync(cancellation);
        }
        async ValueTask<int> EffectAsync(BlueTuskConnection connection, BlueTuskTransaction transaction, string tenant, string product, Guid id, string phase, string idempotency, CancellationToken cancellation)
        {
            await using var command = new BlueTuskCommand($"INSERT INTO {business}.effects AS e VALUES (@tenant,@product,@id,@phase,@idempotency) ON CONFLICT (tenant,product,identity,phase) DO UPDATE SET idempotency=EXCLUDED.idempotency WHERE e.idempotency=EXCLUDED.idempotency RETURNING idempotency", connection) { Transaction = transaction };
            Add(command, "tenant", tenant); Add(command, "product", product); Add(command, "id", id); Add(command, "phase", phase); Add(command, "idempotency", idempotency);
            Assert.Equal(idempotency, await command.ExecuteScalarAsync<string>(cancellation));
            return 1;
        }
        async Task MeasureAsync(string phase, BlueTuskDataSource source)
        {
            using var process = Process.GetCurrentProcess();
            var pool = routed.GetPoolStatistics();
            string wal = await ScalarAsync<string>(source, "SELECT CASE WHEN pg_is_in_recovery() THEN pg_last_wal_replay_lsn()::text ELSE pg_current_wal_lsn()::text END", token);
            long bytes = await ScalarAsync<long>(source, "SELECT pg_database_size(current_database())", token);
            measurements.Add(new(phase, Stopwatch.GetElapsedTime(started).TotalMilliseconds, wal, bytes, process.TotalProcessorTime.TotalMilliseconds,
                process.WorkingSet64, GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), pool.Total, pool.Busy, pool.Waiting));
        }
        async ValueTask<T> TimedAsync<T>(string name, Func<ValueTask<T>> operation)
        {
            string phase = Volatile.Read(ref promoted) ? "promoted" : "primary";
            long operationStarted = Stopwatch.GetTimestamp();
            try { return await operation(); }
            finally
            {
                var observed = operationSamples.GetOrAdd((phase, name), static _ => new());
                if (observed.Count < 10000) { observed.Enqueue(Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds); }
            }
        }
        OperationMeasurement[] OperationReport() => operationSamples.OrderBy(pair => pair.Key.Phase, StringComparer.Ordinal).ThenBy(pair => pair.Key.Name, StringComparer.Ordinal).Select(pair =>
        {
            double[] ordered = pair.Value.Order().ToArray();
            double Percentile(double value) => ordered[Math.Clamp((int)Math.Ceiling(value * ordered.Length) - 1, 0, ordered.Length - 1)];
            return new OperationMeasurement(pair.Key.Phase, pair.Key.Name, ordered.Length, Percentile(.5), Percentile(.95), Percentile(.99), ordered[^1]);
        }).ToArray();
    }

    private static WorkflowDefinition Definition(string mode) => new()
    {
        Name = mode,
        Version = 1,
        Nodes = mode == "cancel"
            ? [new() { Id = "produce", Kind = WorkflowNodeKind.Activity, Activity = "produce", Compensation = "undo" }, new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "never", DependsOn = ["produce"] }]
            : mode == "signal"
                ? [new() { Id = "produce", Kind = WorkflowNodeKind.Activity, Activity = "produce" }, new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "resume", DependsOn = ["produce"] }, new() { Id = "timer", Kind = WorkflowNodeKind.Timer, Delay = TimeSpan.FromSeconds(8), DependsOn = ["wait"] }, new() { Id = "finish", Kind = WorkflowNodeKind.Activity, Activity = "finish", DependsOn = ["timer", "produce"] }]
                : [new() { Id = "produce", Kind = WorkflowNodeKind.Activity, Activity = "replay" }, new() { Id = "timer", Kind = WorkflowNodeKind.Timer, Delay = TimeSpan.FromMilliseconds(100), DependsOn = ["produce"] }, new() { Id = "finish", Kind = WorkflowNodeKind.Activity, Activity = "finish", DependsOn = ["timer", "produce"] }],
    };

    private static JobWorkerOptions WorkerOptions(int concurrency) => new()
    {
        Concurrency = concurrency,
        ClaimBatchSize = concurrency,
        LeaseDuration = TimeSpan.FromSeconds(5),
        HeartbeatInterval = TimeSpan.FromMilliseconds(500),
        PollInterval = TimeSpan.FromMilliseconds(20),
        StoreFailureBackoff = TimeSpan.FromMilliseconds(100),
        DispatchRecurringSchedules = false,
        RetryPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(20), MaximumDelay = TimeSpan.FromMilliseconds(20), JitterFraction = 0 },
    };
    private const string RecoveryConfiguration = "Connect timeout=1s per connection attempt; target=ReadWrite; no configured host-recheck interval (tested provider refreshes host state on checkout); pool maximum=8 per host/16 aggregate; Jobs concurrency=4/tenant; Workflows concurrency=3/tenant; pre-promotion noncritical handler gate; active attempt-two acknowledgement barrier for stale-owner probes; lease=5s; heartbeat=500ms; store backoff=100ms; poll=20ms; drain phase deadline=45s; scenario work deadline=180s with bounded cleanup; final exact Job verification concurrency=4";
}

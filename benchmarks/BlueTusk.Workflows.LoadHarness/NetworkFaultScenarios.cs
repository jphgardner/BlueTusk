using System.Diagnostics;
using BlueTusk.Data;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows.LoadHarness;

internal static partial class Program
{
    private static async Task<FaultResult> NetworkPartitionAsync(string connectionString, bool workflow)
    {
        string suffix = Guid.NewGuid().ToString("N");
        var jobOptions = new JobStoreOptions { Schema = "fault_jobs_" + suffix };
        var workflowOptions = new WorkflowOptions { Schema = "fault_wf_" + suffix };
        await using var adminSource = Source(connectionString, 8);
        var adminStore = new PostgreSqlWorkflowStore(adminSource, workflowOptions, jobOptions);
        using var stop = new CancellationTokenSource();
        Task? worker = null;
        try
        {
            await adminStore.InitializeAsync();
            await ExecuteAsync(adminSource, $"CREATE TABLE \"{workflowOptions.Schema}\".effects (id uuid PRIMARY KEY, tenant varchar(200) NOT NULL, sequence integer NOT NULL)");
            var settings = new BlueTuskConnectionStringBuilder(connectionString);
            await using var network = new NetworkPartitionGate(settings.Host, settings.Port);
            settings.Host = "127.0.0.1";
            settings.Port = network.Port;
            settings.MaximumPoolSize = 4;
            settings.MinimumPoolSize = 0;
            settings.ApplicationName = ApplicationName;
            await using var workerSource = BlueTuskDataSource.Create(settings.ConnectionString);
            var workerStore = new PostgreSqlWorkflowStore(workerSource, workflowOptions, jobOptions);
            try
            {
            using var instruments = new InstrumentProbe();
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int canceledAttempts = 0;
            WorkflowKey? key = null;
            Guid jobId;
            async ValueTask AwaitPartitionCancellationAsync(CancellationToken token)
            {
                firstStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref canceledAttempts);
                    canceled.TrySetResult();
                    throw;
                }
            }

            var workerOptions = FaultWorkerOptions() with { StoreFailureBackoff = TimeSpan.FromMilliseconds(100) };
            if (workflow)
            {
                await adminStore.RegisterDefinitionAsync(FaultScope, FaultWorkflowDefinition());
                key = await adminStore.StartAsync(new WorkflowStartRequest
                {
                    Scope = FaultScope, Definition = "fault", Version = 1,
                    Input = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new LoadPayload(0, 4, 0, ""), HarnessJsonContext.Default.LoadPayload),
                });
                jobId = (await adminStore.ReadNodesAsync(key)).Single(node => node.Id == "effect").JobId!.Value;
                var registry = new WorkflowActivityRegistry().Register("fault-effect.v1", HarnessJsonContext.Default.LoadPayload, HarnessJsonContext.Default.LoadPayload,
                    async (payload, context, token) =>
                    {
                        if (context.Attempt == 1) { await AwaitPartitionCancellationAsync(token); }
                        await using var connection = await workerSource.OpenConnectionAsync(token);
                        await using var transaction = await connection.BeginTransactionAsync(token);
                        await InsertEffectAsync(connection, transaction, workflowOptions.Schema, context.Workflow.Id, FaultScope.Tenant, payload.Sequence, token);
                        await transaction.CommitAsync(token);
                        return payload;
                    });
                worker = new WorkflowWorker(workerStore, FaultScope, "partition-worker", registry,
                    new WorkflowWorkerOptions { Jobs = workerOptions, RecoveryInterval = TimeSpan.FromMilliseconds(100) }).RunAsync(stop.Token);
            }
            else
            {
                jobId = await adminStore.Jobs.EnqueueAsync(JobRequest.FromJson(FaultScope, "fault.v1", new LoadPayload(0, 4, 0, ""), HarnessJsonContext.Default.LoadPayload));
                var registry = new JobHandlerRegistry().Register("fault.v1", HarnessJsonContext.Default.LoadPayload, async (payload, context, token) =>
                {
                    if (context.Attempt == 1) { await AwaitPartitionCancellationAsync(token); }
                    var effect = await workerStore.Jobs.ExecuteFencedAsync(context.Lease!, async (connection, transaction, cancellation) =>
                    {
                        await InsertEffectAsync(connection, transaction, workflowOptions.Schema, context.JobId, FaultScope.Tenant, payload.Sequence, cancellation);
                        return true;
                    }, token);
                    Check(effect.Executed, "partition recovery effect committed with live fence");
                });
                worker = new JobWorker(workerStore.Jobs, FaultScope, "partition-worker", registry, workerOptions).RunAsync(stop.Token);
            }

            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var original = (await adminStore.Jobs.ReadAsync(FaultScope, jobId))!;
            Check(original is { Status: JobStatus.Running, Attempts: 1 }, "partition begins during first attempt");
            network.Partition(true);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The blocked replacement window outlasts the last one-second database lease.
            await Task.Delay(1600);
            var stale = new JobLease(jobId, FaultScope, original.JobType, ReadOnlyMemory<byte>.Empty, 1, original.MaximumAttempts,
                "partition-worker", original.FencingToken, DateTimeOffset.UtcNow.AddSeconds(1));
            Check(!await adminStore.Jobs.HeartbeatAsync(stale, TimeSpan.FromSeconds(1)), "isolated owner cannot revive expired lease");
            var recovery = Stopwatch.StartNew();
            network.Partition(false);
            await WaitUntilAsync(async () =>
            {
                bool complete = workflow ? (await adminStore.ReadAsync(key!))!.Status == WorkflowStatus.Succeeded
                    : (await adminStore.Jobs.ReadAsync(FaultScope, jobId))!.Status == JobStatus.Succeeded;
                var health = await adminStore.Jobs.InspectAsync(FaultScope, 1);
                return complete && health.PendingObserved == 0 && health.RunningObserved == 0;
            }, TimeSpan.FromSeconds(10));
            recovery.Stop();
            await stop.CancelAsync();
            await worker;
            var completed = (await adminStore.Jobs.ReadAsync(FaultScope, jobId))!;
            int effects = await EffectCountAsync(adminSource, workflowOptions.Schema);
            var metrics = instruments.Snapshot();
            double failures = metrics.Where(observation => observation.Name == "bluetusk.jobs.store_failures").Sum(observation => observation.Sum);
            Check(completed is { Status: JobStatus.Succeeded, Attempts: 2 } && completed.FencingToken > original.FencingToken && effects == 1,
                "partition recovery advances fence and produces one durable effect");
            Check(canceledAttempts == 1 && failures > 0 && network.RejectedConnections > 0 && network.CutConnections > 0
                && network.PeakActiveConnections <= 4, "partition cancellation telemetry and bounded connections");
            Check(!await adminStore.Jobs.CompleteAsync(stale), "partition stale acknowledgement rejected");
            if (workflow) { Check((await adminStore.ReplayAsync(key!))!.MatchesPersistedState, "partition workflow history replays"); }
            return new(workflow ? "workflow-network-partition-and-heal" : "jobs-network-partition-and-heal", true,
                recovery.Elapsed.TotalMilliseconds, completed.Attempts, effects, false, canceledAttempts,
                network.RejectedConnections, network.PeakActiveConnections, failures);
            }
            finally
            {
                await stop.CancelAsync();
                if (worker is not null) { await worker; }
            }
        }
        finally
        {
            await stop.CancelAsync();
            if (worker is not null) { await worker; }
            await ExecuteAsync(adminSource, $"DROP SCHEMA IF EXISTS \"{workflowOptions.Schema}\" CASCADE; DROP SCHEMA IF EXISTS \"{jobOptions.Schema}\" CASCADE");
        }
    }
}

using System.Diagnostics;
using System.Reflection;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows.LoadHarness;

internal static partial class Program
{
    private static JobWorkerOptions FaultWorkerOptions() => new()
    {
        Concurrency = 1,
        ClaimBatchSize = 1,
        LeaseDuration = TimeSpan.FromSeconds(1),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        PollInterval = TimeSpan.FromMilliseconds(10),
        DispatchRecurringSchedules = false,
    };

    private static WorkflowDefinition FaultWorkflowDefinition() => new()
    {
        Name = "fault",
        Version = 1,
        Nodes =
        [
            new() { Id = "effect", Kind = WorkflowNodeKind.Activity, Activity = "fault-effect.v1" },
            new() { Id = "timer", Kind = WorkflowNodeKind.Timer, Delay = TimeSpan.FromMilliseconds(100), DependsOn = ["effect"] },
        ],
    };

    private static async Task<int> FaultWorkflowWorkerAsync(string connectionString, string jobsSchema, string workflowSchema)
    {
        Check(jobsSchema.StartsWith("fault_jobs_", StringComparison.Ordinal) && workflowSchema.StartsWith("fault_wf_", StringComparison.Ordinal)
            && jobsSchema.Length == 43 && workflowSchema.Length == 41
            && jobsSchema[11..].All(char.IsAsciiHexDigit) && workflowSchema[9..].All(char.IsAsciiHexDigit), "workflow child schema identity");
        await using var source = Source(connectionString, 4);
        var store = new PostgreSqlWorkflowStore(source, new WorkflowOptions { Schema = workflowSchema }, new JobStoreOptions { Schema = jobsSchema });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var registry = new WorkflowActivityRegistry().Register("fault-effect.v1", HarnessJsonContext.Default.LoadPayload, HarnessJsonContext.Default.LoadPayload,
            async (payload, context, token) =>
            {
                // Deliberately model an external idempotent effect, committed before the workflow result.
                await using (var connection = await source.OpenConnectionAsync(token))
                await using (var transaction = await connection.BeginTransactionAsync(token))
                {
                    await InsertEffectAsync(connection, transaction, workflowSchema, context.Workflow.Id, context.Workflow.Scope.Tenant, payload.Sequence, token);
                    await transaction.CommitAsync(token);
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return payload;
            });
        await new WorkflowWorker(store, FaultScope, "fault-wf-child", registry,
            new WorkflowWorkerOptions { Jobs = FaultWorkerOptions(), RecoveryInterval = TimeSpan.FromMilliseconds(100) }).RunAsync(deadline.Token);
        return 0;
    }

    private static async Task<FaultResult> WorkflowProcessDeathAsync(string connectionString)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string jobsSchema = "fault_jobs_" + suffix;
        string workflowSchema = "fault_wf_" + suffix;
        await using var source = Source(connectionString, 8);
        var store = new PostgreSqlWorkflowStore(source, new WorkflowOptions { Schema = workflowSchema }, new JobStoreOptions { Schema = jobsSchema });
        Process? child = null;
        Task<string>? output = null;
        Task<string>? error = null;
        try
        {
            await store.InitializeAsync();
            await ExecuteAsync(source, $"CREATE TABLE \"{workflowSchema}\".effects (id uuid PRIMARY KEY, tenant varchar(200) NOT NULL, sequence integer NOT NULL)");
            await store.RegisterDefinitionAsync(FaultScope, FaultWorkflowDefinition());
            var key = await store.StartAsync(new WorkflowStartRequest
            {
                Scope = FaultScope,
                Definition = "fault",
                Version = 1,
                Input = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new LoadPayload(0, 3, 0, ""), HarnessJsonContext.Default.LoadPayload),
            });
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process executable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            }

            start.ArgumentList.Add("--fault-workflow-worker");
            start.ArgumentList.Add(jobsSchema);
            start.ArgumentList.Add(workflowSchema);
            child = Process.Start(start) ?? throw new InvalidOperationException("Workflow fault process did not start.");
            output = child.StandardOutput.ReadToEndAsync();
            error = child.StandardError.ReadToEndAsync();
            await WaitUntilAsync(async () =>
            {
                Check(!child.HasExited, "workflow fault child remains alive until killed");
                return await EffectCountAsync(source, workflowSchema) == 1;
            }, TimeSpan.FromSeconds(20));
            var originalNode = (await store.ReadNodesAsync(key)).Single(node => node.Id == "effect");
            Check(originalNode is { Status: WorkflowNodeStatus.Running, JobId: not null }, "child prepared durable workflow activity");
            var originalJob = (await store.Jobs.ReadAsync(FaultScope, originalNode.JobId!.Value))!;
            Check(originalJob.Attempts == 1, "first workflow attempt running");
            var recovery = Stopwatch.StartNew();
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            _ = await output;
            _ = await error;
            var restarted = new PostgreSqlWorkflowStore(source, new WorkflowOptions { Schema = workflowSchema }, new JobStoreOptions { Schema = jobsSchema });
            var registry = new WorkflowActivityRegistry().Register("fault-effect.v1", HarnessJsonContext.Default.LoadPayload, HarnessJsonContext.Default.LoadPayload,
                async (payload, context, token) =>
                {
                    await using var connection = await source.OpenConnectionAsync(token);
                    await using var transaction = await connection.BeginTransactionAsync(token);
                    await InsertEffectAsync(connection, transaction, workflowSchema, context.Workflow.Id, context.Workflow.Scope.Tenant, payload.Sequence, token);
                    await transaction.CommitAsync(token);
                    return payload;
                });
            using var stop = new CancellationTokenSource();
            Task worker = new WorkflowWorker(restarted, FaultScope, "fault-wf-restarted", registry,
                new WorkflowWorkerOptions { Jobs = FaultWorkerOptions(), RecoveryInterval = TimeSpan.FromMilliseconds(100) }).RunAsync(stop.Token);
            try
            {
                await WaitUntilAsync(async () => (await restarted.ReadAsync(key))!.Status == WorkflowStatus.Succeeded, TimeSpan.FromSeconds(15));
            }
            finally
            {
                await stop.CancelAsync();
                await worker;
            }

            recovery.Stop();
            var completed = (await restarted.Jobs.ReadAsync(FaultScope, originalNode.JobId.Value))!;
            var nodes = await restarted.ReadNodesAsync(key);
            int effects = await EffectCountAsync(source, workflowSchema);
            Check(completed is { Status: JobStatus.Succeeded, Attempts: 2 } && completed.FencingToken > originalJob.FencingToken,
                "workflow activity reclaimed with newer fence");
            Check(nodes.All(node => node.Status == WorkflowNodeStatus.Completed) && effects == 1, "workflow timer resumes and idempotent effect remains singular");
            Check((await restarted.ReplayAsync(key))!.MatchesPersistedState, "workflow history replays after actual process death");
            var stale = new JobLease(completed.JobId, FaultScope, completed.JobType, ReadOnlyMemory<byte>.Empty, 1,
                completed.MaximumAttempts, "fault-wf-child", originalJob.FencingToken, DateTimeOffset.UtcNow.AddSeconds(1));
            Check(!await restarted.Jobs.CompleteAsync(stale), "killed workflow worker capability rejected");
            return new("workflow-process-death-before-result", true, recovery.Elapsed.TotalMilliseconds, completed.Attempts, effects, false);
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }

                if (output is not null) { _ = await output; }
                if (error is not null) { _ = await error; }
                child.Dispose();
            }

            await ExecuteAsync(source, $"DROP SCHEMA IF EXISTS \"{workflowSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{jobsSchema}\" CASCADE");
        }
    }
}

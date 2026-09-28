using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Data;
using BlueTusk.Jobs;
using BlueTusk.Jobs.DependencyInjection;
using BlueTusk.Workflows;
using BlueTusk.Workflows.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Workflows.AotSmoke;

internal static class Program
{
    private static async Task<int> Main()
    {
        string? connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("BLUETUSK_TEST_CONNECTION_STRING is required.");
            return 2;
        }

        string suffix = Guid.NewGuid().ToString("N");
        var workflowOptions = new WorkflowOptions { Schema = "wf_aot_" + suffix };
        var jobOptions = new JobStoreOptions { Schema = "jobs_aot_" + suffix };
        string stage = "initialize";
        try
        {
            await using var firstSource = BlueTuskDataSource.Create(connectionString);
            var store = new PostgreSqlWorkflowStore(firstSource, workflowOptions, jobOptions);
            await store.InitializeAsync();
            await ExecuteAsync(firstSource, $"CREATE TABLE \"{workflowOptions.Schema}\".effects (job_id uuid PRIMARY KEY, value integer NOT NULL)");
            stage = "typed job and fenced transaction";
            var jobScope = new JobScope("aot-tenant", "jobs");
            Guid job = await store.Jobs.EnqueueAsync(JobRequest.FromJson(jobScope, "smoke.v1", new SmokePayload(42), SmokeJsonContext.Default.SmokePayload));
            var jobRegistry = new JobHandlerRegistry().Register("smoke.v1", SmokeJsonContext.Default.SmokePayload, async (payload, context, token) =>
            {
                Check(payload.Value == 42 && context.Lease is not null);
                var fenced = await store.Jobs.ExecuteFencedAsync(context.Lease!, async (connection, transaction, cancellation) =>
                {
                    await using var command = new BlueTuskCommand($"INSERT INTO \"{workflowOptions.Schema}\".effects VALUES (@id, @value) ON CONFLICT DO NOTHING", connection)
                    {
                        Transaction = transaction,
                    };
                    command.Parameters.Add(new BlueTuskParameter<Guid>(context.JobId) { ParameterName = "id" });
                    command.Parameters.Add(new BlueTuskParameter<int>(payload.Value) { ParameterName = "value" });
                    return await command.ExecuteNonQueryAsync(cancellation);
                }, token);
                Check(fenced.Executed);
            });
            using (var stop = new CancellationTokenSource())
            {
                Task running = new JobWorker(store.Jobs, jobScope, "aot-jobs", jobRegistry, JobOptions()).RunAsync(stop.Token);
                try
                {
                    await WaitUntilAsync(async () => (await store.Jobs.ReadAsync(jobScope, job))!.Status == JobStatus.Succeeded);
                }
                finally
                {
                    await stop.CancelAsync();
                    await running.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }

            stage = "workflow activity and signal wait";
            var scope = new JobScope("aot-tenant", "workflows");
            await store.RegisterDefinitionAsync(scope, new WorkflowDefinition
            {
                Name = "native-smoke",
                Version = 1,
                Nodes =
                [
                    new() { Id = "produce", Kind = WorkflowNodeKind.Activity, Activity = "produce.v1", Compensation = "undo.v1" },
                    new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "resume", DependsOn = ["produce"] },
                    new() { Id = "timer", Kind = WorkflowNodeKind.Timer, Delay = TimeSpan.FromMilliseconds(100), DependsOn = ["wait"] },
                    new() { Id = "fail", Kind = WorkflowNodeKind.Activity, Activity = "fail.v1", DependsOn = ["timer"], MaximumAttempts = 1 },
                ],
            });
            var key = await store.StartAsync(new WorkflowStartRequest
            {
                Scope = scope,
                Definition = "native-smoke",
                Version = 1,
                Input = JsonSerializer.SerializeToUtf8Bytes(new SmokePayload(42), SmokeJsonContext.Default.SmokePayload),
                DeduplicationKey = "native-run",
            });
            int produced = 0;
            int compensated = 0;
            var activities = new WorkflowActivityRegistry()
                .Register("produce.v1", SmokeJsonContext.Default.SmokePayload, SmokeJsonContext.Default.SmokePayload, (payload, context, token) =>
                {
                    Check(context.Attempt == 1 && !token.IsCancellationRequested);
                    Interlocked.Increment(ref produced);
                    return ValueTask.FromResult(new SmokePayload(payload.Value + 1));
                })
                .Register("fail.v1", (_, _) => ValueTask.FromException<ReadOnlyMemory<byte>>(new JobHandlerException("smoke_failure", retryable: false)))
                .Register("undo.v1", (context, _) =>
                {
                    Check(context.IsCompensation && JsonSerializer.Deserialize(context.ActivityResult.Span, SmokeJsonContext.Default.SmokePayload)?.Value == 43);
                    Interlocked.Increment(ref compensated);
                    return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
                });
            var workerOptions = new WorkflowWorkerOptions
            {
                Jobs = JobOptions(),
                RecoveryInterval = TimeSpan.FromMilliseconds(20),
                RecoveryBatchSize = 32,
            };
            using (var stop = new CancellationTokenSource())
            {
                Task running = new WorkflowWorker(store, scope, "aot-before-restart", activities, workerOptions).RunAsync(stop.Token);
                try
                {
                    await WaitUntilAsync(async () => (await store.ReadNodesAsync(key)).Single(node => node.Id == "produce").Status == WorkflowNodeStatus.Completed);
                }
                finally
                {
                    await stop.CancelAsync();
                    await running.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }

            Check(produced == 1);
            stage = "typed host readiness and generated JSON";
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddJobsReadiness("aot-jobs", jobScope, _ => store.Jobs, new JobHealthCheckOptions());
            services.AddWorkflowsReadiness("aot-workflows", scope, _ => store, new WorkflowHealthCheckOptions());
            await using (var host = services.BuildServiceProvider())
            {
                Check((await host.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Status == HealthStatus.Healthy);
                var readiness = await host.GetRequiredKeyedService<WorkflowScopeHealthCheck>("aot-workflows").ReadAsync();
                Check(readiness.Scope!.RunningObserved == 1);
                string serialized = JsonSerializer.Serialize(readiness, WorkflowScopeHealthCheck.JsonTypeInfo);
                Check(JsonSerializer.Deserialize(serialized, WorkflowScopeHealthCheck.JsonTypeInfo) == readiness);
                var jobReadiness = await host.GetRequiredKeyedService<JobQueueHealthCheck>("aot-jobs").ReadAsync();
                Check(JsonSerializer.Deserialize(JsonSerializer.Serialize(jobReadiness, JobQueueHealthCheck.JsonTypeInfo), JobQueueHealthCheck.JsonTypeInfo) == jobReadiness);
            }
            await firstSource.DisposeAsync();
            stage = "source restart, signal, timer and compensation";
            await using var restartedSource = BlueTuskDataSource.Create(connectionString);
            var restarted = new PostgreSqlWorkflowStore(restartedSource, workflowOptions, jobOptions);
            await restarted.InitializeAsync();
            Check(await restarted.SignalAsync(key, "resume", "resume-1", ReadOnlyMemory<byte>.Empty));
            using (var stop = new CancellationTokenSource())
            {
                Task running = new WorkflowWorker(restarted, scope, "aot-after-restart", activities, workerOptions).RunAsync(stop.Token);
                try
                {
                    await WaitUntilAsync(async () => (await restarted.ReadAsync(key))!.Status == WorkflowStatus.Failed);
                    Check(produced == 1 && compensated == 1);
                    Check((await restarted.ReadAsync(key))!.FailureCode == "smoke_failure");
                    Check((await restarted.ReplayAsync(key))!.MatchesPersistedState);
                }
                finally
                {
                    await stop.CancelAsync();
                    await running.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }

            Console.WriteLine("BlueTusk Jobs/Workflows NativeAOT smoke passed: typed handler, fenced effect, host readiness/generated JSON, restart, signal, timer, compensation, replay.");
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("BlueTusk Jobs/Workflows smoke failed at: " + stage);
            return 1;
        }
        finally
        {
            await using var cleanup = BlueTuskDataSource.Create(connectionString);
            await ExecuteAsync(cleanup, $"DROP SCHEMA IF EXISTS \"{workflowOptions.Schema}\" CASCADE");
            await ExecuteAsync(cleanup, $"DROP SCHEMA IF EXISTS \"{jobOptions.Schema}\" CASCADE");
        }
    }

    private static JobWorkerOptions JobOptions() => new()
    {
        Concurrency = 4,
        ClaimBatchSize = 32,
        LeaseDuration = TimeSpan.FromSeconds(10),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        PollInterval = TimeSpan.FromMilliseconds(10),
        DispatchRecurringSchedules = false,
        RetryPolicy = new JobRetryPolicy { InitialDelay = TimeSpan.FromMilliseconds(10), MaximumDelay = TimeSpan.FromMilliseconds(10), JitterFraction = 0 },
    };

    private static void Check(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("A smoke invariant failed.");
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(20));
            await Task.Delay(20);
        }
    }

    private static async Task ExecuteAsync(BlueTuskDataSource source, string sql)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new BlueTuskCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}

internal sealed record SmokePayload(int Value);

[JsonSerializable(typeof(SmokePayload))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;

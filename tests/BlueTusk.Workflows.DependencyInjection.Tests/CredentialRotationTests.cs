using System.Text.Json.Serialization;
using BlueTusk.Data;
using BlueTusk.Jobs;
using BlueTusk.Jobs.DependencyInjection;
using BlueTusk.Workflows.Tests;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Workflows.DependencyInjection.Tests;

public sealed class CredentialRotationTests
{
    [Fact]
    public async Task OwnedPasswordProviderRotationAndSessionDrainRecoverFencedJobAndPausedWorkflow()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE TABLE {schema}.effects (identity uuid PRIMARY KEY, kind text NOT NULL)");
        await using var role = await OwnedOperationalRole.CreateAsync(database);
        await role.GrantRuntimeAsync();
        var store = new PostgreSqlWorkflowStore(role.Source, database.Options, database.JobOptions);
        var jobScope = new JobScope("rotation-owned", "jobs");
        Guid jobId = await store.Jobs.EnqueueAsync(JobRequest.FromJson(jobScope, "rotate", new RotationPayload(42), RotationJsonContext.Default.RotationPayload));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobLease? firstLease = null;
        var handlers = new JobHandlerRegistry().Register("rotate", RotationJsonContext.Default.RotationPayload, async (payload, context, token) =>
        {
            Assert.Equal(42, payload.Value);
            var effect = await store.Jobs.ExecuteFencedAsync(context.Lease!, async (connection, transaction, cancellation) =>
            {
                await InsertEffectAsync(connection, transaction, database.Options.Schema, context.JobId, "job", cancellation);
                if (context.Attempt == 1)
                {
                    firstLease = context.Lease;
                    entered.TrySetResult();
                    await using var hold = new BlueTuskCommand("SELECT pg_sleep(30)", connection) { Transaction = transaction };
                    _ = await hold.ExecuteNonQueryAsync(cancellation);
                }
                return true;
            }, token);
            Assert.True(effect.Executed);
        });
        using (var stop = new CancellationTokenSource())
        {
            Task worker = new JobWorker(store.Jobs, jobScope, "rotation", handlers, WorkflowDatabase.WorkerOptions.Jobs with
            {
                Concurrency = 1,
                ClaimBatchSize = 1,
                LeaseDuration = TimeSpan.FromSeconds(1),
                HeartbeatInterval = TimeSpan.FromMilliseconds(100),
                StoreFailureBackoff = TimeSpan.FromMilliseconds(20),
            }).RunAsync(stop.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                int callsBefore = role.ProviderCalls;
                await using var oldCredential = role.CreateFixedCredentialSource();
                await role.RotateAsync();
                await role.TerminateOwnedSessionsAsync();
                _ = await Assert.ThrowsAsync<BlueTuskException>(() => oldCredential.OpenConnectionAsync().AsTask());
                var staleStore = new PostgreSqlWorkflowStore(oldCredential, database.Options, database.JobOptions);
                var staleProbe = await new JobQueueHealthCheck(staleStore.Jobs, jobScope, new JobHealthCheckOptions()).CheckHealthAsync(new HealthCheckContext());
                Assert.Equal(HealthStatus.Unhealthy, staleProbe.Status);
                Assert.Null(staleProbe.Exception);
                Assert.Empty(staleProbe.Data);
                await WorkflowDatabase.WaitUntilAsync(async () => (await store.Jobs.ReadAsync(jobScope, jobId))!.Status == JobStatus.Succeeded);
                var completed = (await store.Jobs.ReadAsync(jobScope, jobId))!;
                Assert.True(completed.Attempts >= 2);
                Assert.True(completed.FencingToken > firstLease!.FencingToken);
                Assert.False(await store.Jobs.CompleteAsync(firstLease));
                Assert.True(role.ProviderCalls > callsBefore);
                Assert.Equal(1L, await CountEffectsAsync(database, "job"));
            }
            finally
            {
                await stop.CancelAsync();
                await worker.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        var scope = new JobScope("rotation-owned", "workflows");
        await store.RegisterDefinitionAsync(scope, new WorkflowDefinition
        {
            Name = "rotation",
            Version = 1,
            Nodes =
            [
                new() { Id = "before", Kind = WorkflowNodeKind.Activity, Activity = "effect" },
                new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "resume", DependsOn = ["before"] },
                new() { Id = "timer", Kind = WorkflowNodeKind.Timer, Delay = TimeSpan.FromMilliseconds(100), DependsOn = ["wait"] },
                new() { Id = "after", Kind = WorkflowNodeKind.Activity, Activity = "finish", DependsOn = ["timer", "before"] },
            ],
        });
        var key = await store.StartAsync(new WorkflowStartRequest { Scope = scope, Definition = "rotation", Version = 1, Input = ReadOnlyMemory<byte>.Empty });
        int effectCalls = 0;
        int finishCalls = 0;
        var activities = new WorkflowActivityRegistry()
            .RegisterTransactional("effect", async (connection, transaction, context, token) =>
            {
                Interlocked.Increment(ref effectCalls);
                await InsertEffectAsync(connection, transaction, database.Options.Schema, context.Workflow.Id, "workflow", token);
                return new byte[] { 7 };
            })
            .Register("finish", (context, _) =>
            {
                Interlocked.Increment(ref finishCalls);
                Assert.Equal(7, context.DependencyResults["before"].Span[0]);
                return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
            });
        using (var stop = new CancellationTokenSource())
        {
            Task worker = new WorkflowWorker(store, scope, "before-rotation", activities, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
            try { await WorkflowDatabase.WaitUntilAsync(async () => (await store.ReadNodesAsync(key)).Single(node => node.Id == "before").Status == WorkflowNodeStatus.Completed); }
            finally { await stop.CancelAsync(); await worker.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        int beforeSecondRotation = role.ProviderCalls;
        await role.RotateAsync();
        await role.TerminateOwnedSessionsAsync();
        var readiness = new WorkflowScopeHealthCheck(store, scope, new WorkflowHealthCheckOptions());
        Assert.Equal(HealthStatus.Healthy, (await readiness.ReadAsync()).Status);
        Assert.True(await store.SignalAsync(key, "resume", "rotation-resume", ReadOnlyMemory<byte>.Empty));
        using (var stop = new CancellationTokenSource())
        {
            Task worker = new WorkflowWorker(store, scope, "after-rotation", activities, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
            try { await WorkflowDatabase.WaitUntilAsync(async () => (await store.ReadAsync(key))!.Status == WorkflowStatus.Succeeded); }
            finally { await stop.CancelAsync(); await worker.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        Assert.Equal(1, effectCalls);
        Assert.Equal(1, finishCalls);
        Assert.Equal(1L, await CountEffectsAsync(database, "workflow"));
        Assert.True(role.ProviderCalls > beforeSecondRotation);
        Assert.True((await store.ReplayAsync(key))!.MatchesPersistedState);
        Assert.Equal(HealthStatus.Healthy, (await readiness.ReadAsync()).Status);
    }

    private static async Task InsertEffectAsync(BlueTuskConnection connection, BlueTuskTransaction transaction, string schema, Guid identity, string kind, CancellationToken token)
    {
        await using var command = new BlueTuskCommand($"INSERT INTO \"{schema}\".effects VALUES (@id, @kind)", connection) { Transaction = transaction };
        command.Parameters.Add(new BlueTuskParameter<Guid>(identity) { ParameterName = "id" });
        command.Parameters.Add(new BlueTuskParameter<string>(kind) { ParameterName = "kind" });
        _ = await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<long> CountEffectsAsync(WorkflowDatabase database, string kind)
    {
        await using var connection = await database.Source.OpenConnectionAsync();
        await using var command = new BlueTuskCommand($"SELECT count(*) FROM \"{database.Options.Schema}\".effects WHERE kind = @kind", connection);
        command.Parameters.Add(new BlueTuskParameter<string>(kind) { ParameterName = "kind" });
        return await command.ExecuteScalarAsync<long>(CancellationToken.None);
    }
}

internal sealed record RotationPayload(int Value);
[JsonSerializable(typeof(RotationPayload))]
internal sealed partial class RotationJsonContext : JsonSerializerContext;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Jobs;
using BlueTusk.Workflows;
using static BlueTusk.UpgradeProbe.ProbeContext;

namespace BlueTusk.UpgradeProbe;

/// <summary>
/// Workflows rehearsal. The baseline starts three two-activity workflows in separate queues and stops
/// with one untouched, one whose first activity attempt failed for retry and one whose first activity
/// lease it still holds (a crashed worker). The candidate must keep both durable formats, return the
/// original workflows for duplicate starts, honour the held lease until it expires, run every activity
/// exactly once with a stable idempotency key and a replay matching persisted state, and keep the
/// baseline's attempt history. The baseline then reopens format 1, reads the results and runs a
/// workflow the candidate left pending.
/// </summary>
internal static class FamilyProbe
{
    public const string Family = "Workflows";
    private const string ActivityA = "probe.a.v1";
    private const string ActivityB = "probe.b.v1";
    private static readonly string TypeA = "bluetusk.workflow.activity." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ActivityA)));
    private static readonly WorkflowDefinition Definition = new()
    {
        Name = "upgrade",
        Version = 1,
        Nodes =
        [
            new WorkflowNode { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = ActivityA },
            new WorkflowNode { Id = "b", Kind = WorkflowNodeKind.Activity, Activity = ActivityB, DependsOn = ["a"] },
        ],
    };

    public static async Task RunAsync(ProbeContext context)
    {
        var workflowSchema = context.SchemaBase + "_workflows";
        var jobSchema = context.SchemaBase + "_jobs";
        var store = new PostgreSqlWorkflowStore(context.DataSource, new WorkflowOptions { Schema = workflowSchema },
            new JobStoreOptions { Schema = jobSchema });
        await context.FingerprintAsync("before", workflowSchema, jobSchema);
        await store.InitializeAsync();
        await context.FingerprintAsync("after", workflowSchema, jobSchema);
        context.Observe("WorkflowFormat", await context.CountAsync($"SELECT format FROM \"{workflowSchema}\".settings"));
        context.Observe("JobsFormat", await context.CountAsync($"SELECT format_version FROM \"{jobSchema}\".settings"));
        var heldExpiry = $"SELECT max(lease_expires) FROM \"{jobSchema}\".jobs WHERE lease_owner = 'baseline-held'";

        if (context.IsSeed)
        {
            var state = new Dictionary<string, string>();
            foreach (var queue in new[] { "w1", "w2", "w3" })
            {
                await store.RegisterDefinitionAsync(Scope(queue), Definition);
                state[queue] = (await store.StartAsync(Start(queue))).Id.ToString("N");
            }

            context.Observe("WorkflowsStarted", state.Count);
            var retry = await ClaimOneAsync(store, "w2", "baseline-retry");
            Require(await store.Jobs.FailAsync(retry, "baseline_retry", TimeSpan.Zero), "The baseline could not persist a retryable failure.");
            context.Observe("RetriedActivities", 1);
            // The baseline stops while holding this lease: the candidate inherits an in-flight activity.
            _ = await ClaimOneAsync(store, "w3", "baseline-held");
            context.Observe("HeldLeases", await context.CountAsync(
                $"SELECT count(*) FROM \"{jobSchema}\".jobs WHERE lease_owner = 'baseline-held' AND lease_expires > clock_timestamp()"));
            context.WriteState(state);
            return;
        }

        var handoff = context.ReadState();
        if (context.IsUpgrade)
        {
            var deduplicated = 0;
            foreach (var queue in new[] { "w1", "w2", "w3" })
            {
                deduplicated += (await store.StartAsync(Start(queue))).Id.ToString("N") == handoff[queue] ? 1 : 0;
            }

            context.Observe("DeduplicatedStarts", deduplicated);
            await context.RequireUnexpiredAsync(heldExpiry, "baseline activity lease");
            Require((await store.Jobs.ClaimAsync(Scope("w3"), "candidate-probe", 1, InFlightLease, [TypeA])).Count == 0,
                "The candidate claimed an activity the baseline still held.");
            context.Observe("HeldLeaseHonoured", 1);
            var executions = await RunWorkersAsync(store, handoff, ["w1", "w2", "w3"], "candidate-worker");
            context.Observe("ActivityExecutions", executions.Values.Sum());
            context.Observe("DistinctActivityKeys", executions.Count);
            context.Observe("SucceededWorkflows", await CountSucceededAsync(store, handoff, ["w1", "w2", "w3"]));
            context.Observe("ReplayMatches", await CountReplayMatchesAsync(store, handoff, ["w1", "w2", "w3"]));
            var retried = (await store.ReadNodesAsync(Key("w2", handoff))).Single(node => node.Id == "a");
            var history = await store.Jobs.ReadHistoryAsync(Scope("w2"), retried.JobId ?? Guid.Empty);
            Require(history.OrderBy(attempt => attempt.Attempt).Select(attempt => attempt.Outcome).SequenceEqual(["failed", "succeeded"]),
                "The baseline's failed attempt was lost or the retry was duplicated.");
            context.Observe("RetryHistoryPreserved", 1);
            await RequireNewerFormatRejectedAsync(context);
            await store.RegisterDefinitionAsync(Scope("w4"), Definition);
            handoff["w4"] = (await store.StartAsync(Start("w4"))).Id.ToString("N");
            context.Observe("WorkflowsStarted", 1);
            context.WriteState(handoff);
            return;
        }

        Require(await CountSucceededAsync(store, handoff, ["w1", "w2", "w3"]) == 3, "Candidate-completed workflows were not readable after rollback.");
        var rolledBack = await RunWorkersAsync(store, handoff, ["w4"], "rollback-worker");
        context.Observe("ActivityExecutions", rolledBack.Values.Sum());
        context.Observe("DistinctActivityKeys", rolledBack.Count);
        context.Observe("SucceededWorkflows", await CountSucceededAsync(store, handoff, ["w1", "w2", "w3", "w4"]));
        context.Observe("ReplayMatches", await CountReplayMatchesAsync(store, handoff, ["w1", "w2", "w3", "w4"]));
    }

    private static JobScope Scope(string queue) => new("upgrade", queue);

    private static WorkflowKey Key(string queue, Dictionary<string, string> state) => new(Scope(queue), Guid.ParseExact(state[queue], "N"));

    private static WorkflowStartRequest Start(string queue) => new()
    {
        Scope = Scope(queue),
        Definition = Definition.Name,
        Version = Definition.Version,
        Input = Encoding.UTF8.GetBytes("input-" + queue),
        DeduplicationKey = "start-" + queue,
    };

    private static async Task<JobLease> ClaimOneAsync(PostgreSqlWorkflowStore store, string queue, string owner)
    {
        var leases = await store.Jobs.ClaimAsync(Scope(queue), owner, 1, InFlightLease, [TypeA]);
        Require(leases.Count == 1, $"Expected one first-activity lease in {queue}.");
        return leases[0];
    }

    private static async Task<ConcurrentDictionary<string, int>> RunWorkersAsync(PostgreSqlWorkflowStore store,
        Dictionary<string, string> state, string[] queues, string owner)
    {
        var executions = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        ValueTask<ReadOnlyMemory<byte>> Execute(WorkflowActivityContext activity)
        {
            executions.AddOrUpdate(activity.IdempotencyKey, 1, static (_, count) => count + 1);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(activity.NodeId));
        }

        var registry = new WorkflowActivityRegistry()
            .Register(ActivityA, (activity, _) => Execute(activity))
            .Register(ActivityB, (activity, _) => Execute(activity));
        var options = new WorkflowWorkerOptions
        {
            Jobs = new JobWorkerOptions
            {
                Concurrency = 2,
                PollInterval = TimeSpan.FromMilliseconds(100),
                LeaseDuration = TimeSpan.FromSeconds(30),
                HeartbeatInterval = TimeSpan.FromSeconds(5),
                DispatchRecurringSchedules = false,
            },
        };
        using var stop = new CancellationTokenSource();
        var workers = queues.Select(queue => new WorkflowWorker(store, Scope(queue), owner, registry, options).RunAsync(stop.Token)).ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(InFlightLeaseSeconds + 60);
        try
        {
            while (await CountSucceededAsync(store, state, queues) < queues.Length)
            {
                Require(workers.All(worker => !worker.IsCompleted), "A workflow worker stopped before its workflows completed.");
                Require(DateTime.UtcNow < deadline, "Workflows did not complete within the in-flight lease plus one minute.");
                await Task.Delay(200);
            }
        }
        finally
        {
            await stop.CancelAsync();
            foreach (var worker in workers)
            {
                try
                {
                    await worker;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        Require(executions.Values.All(count => count == 1), "An activity ran more than once across the binary boundary.");
        return executions;
    }

    private static async Task<int> CountSucceededAsync(PostgreSqlWorkflowStore store, Dictionary<string, string> state, string[] queues)
    {
        var succeeded = 0;
        foreach (var queue in queues)
        {
            succeeded += (await store.ReadAsync(Key(queue, state)))?.Status == WorkflowStatus.Succeeded ? 1 : 0;
        }

        return succeeded;
    }

    private static async Task<int> CountReplayMatchesAsync(PostgreSqlWorkflowStore store, Dictionary<string, string> state, string[] queues)
    {
        var matches = 0;
        foreach (var queue in queues)
        {
            matches += (await store.ReplayAsync(Key(queue, state)))?.MatchesPersistedState == true ? 1 : 0;
        }

        return matches;
    }

    private static async Task RequireNewerFormatRejectedAsync(ProbeContext context)
    {
        // workflows/README.md: durable limits and format 1 are fingerprinted; incompatible stores fail initialization.
        var future = context.SchemaBase + "_future";
        var futureJobs = context.SchemaBase + "_future_jobs";
        var store = new PostgreSqlWorkflowStore(context.DataSource, new WorkflowOptions { Schema = future }, new JobStoreOptions { Schema = futureJobs });
        await store.InitializeAsync();
        await context.ExecuteAsync($"UPDATE \"{future}\".settings SET format = format + 1");
        await RequireRejectedAsync<InvalidOperationException>(async () => await store.InitializeAsync(),
            "The candidate initialized a Workflows format newer than it supports.");
        await context.DropSchemaAsync(future);
        await context.DropSchemaAsync(futureJobs);
        context.Observe("NewerFormatRejected", 1);
    }
}

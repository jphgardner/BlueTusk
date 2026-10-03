using System.Globalization;
using BlueTusk.Schema;
using static BlueTusk.UpgradeProbe.ProbeContext;

namespace BlueTusk.UpgradeProbe;

/// <summary>
/// Schema deployment-journal rehearsal. The baseline registers an additive column deployment, verifies
/// the baseline, journals the expand DDL and stops with the verify-target attempt pending under a held
/// lease; it also registers an index-removal deployment under its own planning rules. The candidate must
/// keep journal format 1, honour the lease until it expires, resume the pending attempt (not start a new
/// one), reject the stale fence and finish the deployment. Registration persists plan identity, so a
/// deployment whose plan the candidate's rules change must be refused in each direction.
/// </summary>
internal static class FamilyProbe
{
    public const string Family = "Schema";
    private const string Additive = "upgrade-additive";
    private const string IndexRemoval = "upgrade-index-removal";
    private const string CandidateIndexRemoval = "upgrade-index-removal-candidate";

    public static async Task RunAsync(ProbeContext context)
    {
        var journal = context.SchemaBase + "_journal";
        var app = context.SchemaBase + "_app";
        var coordinator = new PostgreSqlSchemaDeploymentCoordinator(context.DataSource, journal);
        await context.FingerprintAsync("before", journal);
        await coordinator.InitializeAsync();
        await context.FingerprintAsync("after", journal);
        context.Observe("JournalFormat", await context.CountAsync($"SELECT format_version FROM \"{journal}\".settings"));
        var additive = AdditiveDefinition(app);
        var removal = IndexRemovalDefinition(app);
        var leaseExpiry = $"SELECT lease_expires FROM \"{journal}\".deployments WHERE deployment_id = '{Additive}'";
        var steps = additive.Plan.Steps.Select(step => step.Id).ToArray();
        var verifyTarget = additive.Plan.Steps.Single(step => step.Phase == SchemaDeploymentPhase.VerifyTarget).Id;
        var split = Array.IndexOf(steps, verifyTarget);

        if (context.IsSeed)
        {
            await context.ExecuteAsync($"CREATE SCHEMA \"{app}\"; CREATE TABLE \"{app}\".items (id integer NOT NULL)");
            await coordinator.RegisterAsync(Additive, additive);
            await coordinator.RegisterAsync(IndexRemoval, removal);
            context.Observe("RegisteredDeployments", await context.CountAsync($"SELECT count(*) FROM \"{journal}\".deployments"));
            var lease = await coordinator.AcquireAsync(Additive, additive, "baseline-deployer", InFlightLease)
                ?? throw new InvalidOperationException("The baseline could not acquire the deployment lease.");
            var completed = 0;
            foreach (var step in additive.Plan.Steps.Take(split))
            {
                await CompleteAsync(coordinator, lease, additive, step);
                completed++;
            }

            context.Observe("CompletedSteps", completed);
            var pending = await coordinator.BeginExternalStepAsync(lease, additive, verifyTarget);
            Require(pending is { IsNew: true, State: SchemaDeploymentAttemptState.Pending }, "The baseline did not start a pending verify-target attempt.");
            // The baseline stops while the attempt is pending and its lease is held.
            context.Observe("PendingSteps", await context.CountAsync($"SELECT count(*) FROM \"{journal}\".step_attempts WHERE state = 0"));
            context.Observe("HeldLeases", await context.CountAsync(
                $"SELECT count(*) FROM \"{journal}\".deployments WHERE lease_owner = 'baseline-deployer' AND lease_expires > clock_timestamp()"));
            context.WriteState(new()
            {
                ["fence"] = lease.FencingToken.ToString(CultureInfo.InvariantCulture),
                ["attempt"] = pending.Attempt.ToString(CultureInfo.InvariantCulture),
            });
            return;
        }

        var handoff = context.ReadState();
        if (context.IsUpgrade)
        {
            context.Observe("CompletedStepsRead", await CountCompletedAsync(coordinator, additive, steps));
            await context.RequireUnexpiredAsync(leaseExpiry, "baseline deployment lease");
            Require(await coordinator.AcquireAsync(Additive, additive, "candidate-deployer", InFlightLease) is null,
                "The candidate acquired a deployment lease the baseline still held.");
            context.Observe("HeldLeaseHonoured", 1);
            await context.WaitForExpiryAsync(leaseExpiry, "baseline deployment lease");
            var lease = await coordinator.AcquireAsync(Additive, additive, "candidate-deployer", InFlightLease)
                ?? throw new InvalidOperationException("The candidate could not take over the expired deployment lease.");
            var staleFence = long.Parse(handoff["fence"], CultureInfo.InvariantCulture);
            Require(lease.FencingToken > staleFence, "The candidate takeover did not advance the deployment fence.");
            var resumed = await coordinator.BeginExternalStepAsync(lease, additive, verifyTarget);
            Require(!resumed.IsNew && resumed.State == SchemaDeploymentAttemptState.Pending &&
                resumed.Attempt.ToString(CultureInfo.InvariantCulture) == handoff["attempt"],
                "The candidate started a new attempt instead of resuming the baseline's pending one.");
            context.Observe("PendingResumed", 1);
            var stale = new SchemaDeploymentLease(Additive, "baseline-deployer", staleFence);
            await RequireRejectedAsync<InvalidOperationException>(
                async () => await coordinator.CompleteExternalStepAsync(stale, additive, verifyTarget, resumed.Attempt, Evidence, additive.Plan.AfterFingerprint),
                "The stale baseline fence could still complete the pending attempt.");
            context.Observe("StaleLeaseRejected", 1);
            await coordinator.CompleteExternalStepAsync(lease, additive, verifyTarget, resumed.Attempt, Evidence, additive.Plan.AfterFingerprint);
            var completed = 1;
            foreach (var step in additive.Plan.Steps.Skip(split + 1))
            {
                await CompleteAsync(coordinator, lease, additive, step);
                completed++;
            }

            context.Observe("CompletedSteps", completed);
            Require(await coordinator.ReleaseAsync(lease, additive), "The candidate could not release the deployment lease.");
            context.Observe("ChangedPlanRejected", await RegistrationRejectedAsync(coordinator, IndexRemoval, removal));
            await coordinator.RegisterAsync(CandidateIndexRemoval, removal);
            context.Observe("RegisteredDeployments", 1);
            await RequireNewerFormatRejectedAsync(context);
            return;
        }

        context.Observe("CompletedStepsRead", await CountCompletedAsync(coordinator, additive, steps));
        Require(await context.CountAsync($"SELECT count(*) FROM information_schema.columns WHERE table_schema = '{app}' AND table_name = 'items' AND column_name = 'name'") == 1,
            "The journalled expand DDL is not present after rollback.");
        await coordinator.RegisterAsync(Additive, additive);
        var rollbackLease = await coordinator.AcquireAsync(Additive, additive, "rollback-deployer", InFlightLease)
            ?? throw new InvalidOperationException("The rolled-back binary could not acquire the released deployment lease.");
        context.Observe("LeaseAcquired", 1);
        Require(await coordinator.ReleaseAsync(rollbackLease, additive), "The rolled-back binary could not release the deployment lease.");
        context.Observe("ChangedPlanRejected", await RegistrationRejectedAsync(coordinator, CandidateIndexRemoval, removal));
    }

    private const string Evidence = "0000000000000000000000000000000000000000000000000000000000000001";

    private static SchemaCatalogSnapshot Catalog(string app, bool name, bool index) => new(new([
        new SchemaRelation(new(app, "items"), "r", false, false, "d",
            name ? [new("id", 1, "int4", false), new("name", 2, "text", true)] : [new SchemaColumn("id", 1, "int4", false)],
            indexes: index ? [new SchemaIndex("items_id_idx", $"CREATE INDEX items_id_idx ON {app}.items USING btree (id)", true)] : []),
    ]));

    private static SchemaDeploymentDefinition AdditiveDefinition(string app)
    {
        var plan = SchemaDeploymentPlan.Create(Catalog(app, false, false), Catalog(app, true, false), []);
        return new(plan, plan.Steps.Select(step => step.Phase == SchemaDeploymentPhase.Expand
            ? new SchemaDeploymentAction(step.Id, SchemaDeploymentActionKind.TransactionalSql, $"ALTER TABLE \"{app}\".items ADD COLUMN name text")
            : new SchemaDeploymentAction(step.Id, SchemaDeploymentActionKind.External, "upgrade-probe:" + step.Id)));
    }

    private static SchemaDeploymentDefinition IndexRemovalDefinition(string app)
    {
        // The plan is derived by the running binary's rules, so its identity is a property of that binary.
        var plan = SchemaDeploymentPlan.Create(Catalog(app, false, true), Catalog(app, false, false), []);
        return new(plan, plan.Steps.Select(step => new SchemaDeploymentAction(step.Id, SchemaDeploymentActionKind.External, "upgrade-probe:" + step.Id)));
    }

    private static async Task CompleteAsync(PostgreSqlSchemaDeploymentCoordinator coordinator, SchemaDeploymentLease lease,
        SchemaDeploymentDefinition definition, SchemaDeploymentStep step)
    {
        if (step.Phase == SchemaDeploymentPhase.Expand)
        {
            var executed = await coordinator.ExecuteTransactionalSqlStepAsync(lease, definition, step.Id);
            Require(executed.State == SchemaDeploymentAttemptState.Completed, $"Transactional step {step.Id} did not complete.");
            return;
        }

        var attempt = await coordinator.BeginExternalStepAsync(lease, definition, step.Id);
        Require(attempt.IsNew, $"External step {step.Id} was already started.");
        var observed = step.Phase switch
        {
            SchemaDeploymentPhase.VerifyBaseline => definition.Plan.BeforeFingerprint,
            SchemaDeploymentPhase.VerifyTarget => definition.Plan.AfterFingerprint,
            SchemaDeploymentPhase.ApproveReview => definition.Plan.Fingerprint,
            _ => null,
        };
        await coordinator.CompleteExternalStepAsync(lease, definition, step.Id, attempt.Attempt, Evidence, observed);
    }

    private static async Task<int> CountCompletedAsync(PostgreSqlSchemaDeploymentCoordinator coordinator,
        SchemaDeploymentDefinition definition, string[] steps)
    {
        var completed = 0;
        foreach (var step in steps)
        {
            completed += (await coordinator.ReadStepAsync(Additive, definition, step))?.State == SchemaDeploymentAttemptState.Completed ? 1 : 0;
        }

        return completed;
    }

    private static async Task<int> RegistrationRejectedAsync(PostgreSqlSchemaDeploymentCoordinator coordinator, string id,
        SchemaDeploymentDefinition definition)
    {
        // README.md: registration persists plan and definition fingerprints; reusing an ID with different input fails.
        try
        {
            await coordinator.RegisterAsync(id, definition);
            return 0;
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("immutable definition does not match", StringComparison.Ordinal))
        {
            return 1;
        }
    }

    private static async Task RequireNewerFormatRejectedAsync(ProbeContext context)
    {
        // schema/README.md: initialization checks a persistent format marker and fails closed.
        var future = context.SchemaBase + "_future";
        var coordinator = new PostgreSqlSchemaDeploymentCoordinator(context.DataSource, future);
        await coordinator.InitializeAsync();
        await context.ExecuteAsync($"UPDATE \"{future}\".settings SET format_version = format_version + 1");
        await RequireRejectedAsync<InvalidOperationException>(async () => await coordinator.InitializeAsync(),
            "The candidate initialized a Schema journal format newer than it supports.");
        await context.DropSchemaAsync(future);
        context.Observe("NewerFormatRejected", 1);
    }
}

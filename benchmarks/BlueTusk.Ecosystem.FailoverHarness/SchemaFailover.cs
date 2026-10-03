using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Schema;

namespace BlueTusk.Ecosystem.FailoverHarness;

/// <summary>
/// Schema has no replicated runtime of its own; its failover semantics are restart and fencing
/// safety of the deployment journal. A transactional SQL step commits its DDL and its completed
/// journal record together under a database-clock lease with a persistent fencing token: a
/// retry after an unknown commit reads the durable record, a rolled-back step leaves no record,
/// and only the newest owner may finish a step (docs/schema/README.md). Each disturbance
/// interrupts a column-adding step while PostgreSQL is rewriting the table inside the step
/// transaction. Recovery must leave neither the column nor a completed record, fence the old
/// owner, and let the successor complete the step exactly once; the two journals act as two
/// independent tenants.
/// </summary>
internal sealed class SchemaFailover : FailoverCase
{
    private const int DeploymentsPerJournal = 8;
    private const string InFlight = "a-inflight";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(5);
    private static readonly string[] Journals = ["a", "b"];
    private readonly FailoverFixture _fixture;
    private readonly string _prefix;
    private readonly BlueTuskDataSource _source;
    private readonly PostgreSqlSchemaDeploymentCoordinator[] _coordinators;
    private SchemaDeploymentLease? _oldLease;
    private SchemaDeploymentLease? _newLease;

    private SchemaFailover(FailoverFixture fixture, string application, string prefix)
    {
        _fixture = fixture;
        _prefix = prefix;
        _source = fixture.CreateSource(application, 4);
        _coordinators = [new(_source, prefix + "_ja", 30), new(_source, prefix + "_jb", 30)];
    }

    internal static Task<IReadOnlyList<ScenarioResult>> RunAsync(FailoverFixture fixture, CancellationToken token) =>
        ScenarioDriver.RunAllAsync(fixture, () => new SchemaFailover(fixture, ScenarioDriver.Application, "fo_schema_" + Guid.NewGuid().ToString("N")[..24]),
            [FaultKind.BackendTermination, FaultKind.HostProcessKill, FaultKind.PrimaryCrashRestart, FaultKind.StandbyPromotion], token);

    internal override string Family => "Schema";
    internal override string Semantics => "atomic-ddl-and-journal-step; fenced-deployment-lease; idempotent-step-retry";
    internal override int Tenants => Journals.Length;
    internal override long BarrierKey => 180942166601;
    internal override string[] ChildArguments => [_prefix];
    private string Target => _prefix + "_target";

    internal override async Task PrepareAsync(CancellationToken token)
    {
        var tables = new StringBuilder();
        foreach (string journal in Journals)
        {
            for (int index = 0; index < DeploymentsPerJournal; index++)
            {
                tables.Append(CultureInfo.InvariantCulture, $"CREATE TABLE \"{Target}\".{Table(journal + "-" + Index(index))}(id integer PRIMARY KEY); INSERT INTO \"{Target}\".{Table(journal + "-" + Index(index))} VALUES (1);\n");
            }
        }

        // A volatile column default makes PostgreSQL rewrite the table, so the in-flight step blocks inside its DDL transaction.
        await FailoverFixture.ExecuteAsync(_fixture.Admin, $"""
            CREATE SCHEMA "{Target}";
            {tables}
            CREATE TABLE "{Target}".{Table(InFlight)}(id integer PRIMARY KEY); INSERT INTO "{Target}".{Table(InFlight)} VALUES (1);
            CREATE FUNCTION "{Target}".failover_barrier() RETURNS text LANGUAGE plpgsql VOLATILE AS $function$
            BEGIN PERFORM pg_advisory_xact_lock({BarrierKey.ToString(CultureInfo.InvariantCulture)}); RETURN 'migrated'; END;$function$;
            """, token);
        foreach (var coordinator in _coordinators) { await coordinator.InitializeAsync(token); }
    }

    internal override async Task<int> AcknowledgeAsync(CancellationToken token)
    {
        int acknowledged = await DeployAllAsync(_coordinators, Target, static (_, _) => Task.CompletedTask, token);
        _oldLease = await BeginInFlightAsync(_coordinators[0], Target, "owner-before-fault", token);
        return acknowledged;
    }

    internal override Task StartInFlightAsync(CancellationToken token) =>
        _coordinators[0].ExecuteTransactionalSqlStepAsync(_oldLease!, Definition(Target, InFlight), "expand", token).AsTask();

    internal override async Task<int> ReadChildAcknowledgementsAsync(ChildProcess child, CancellationToken token)
    {
        int acknowledged = await ScenarioDriver.ReadAcknowledgementsAsync(child, DeploymentsPerJournal * Journals.Length, token);
        string[] lease = (await child.ReadLineAsync(ScenarioDriver.Phase, token)).Split(' ');
        FailoverFixture.Check(lease is ["LEASE", _, _], "the child reported its issued deployment lease");
        _oldLease = new SchemaDeploymentLease(InFlight, lease[1], long.Parse(lease[2], CultureInfo.InvariantCulture));
        return acknowledged;
    }

    internal override async Task CheckRolledBackAsync(ScenarioRecorder recorder, CancellationToken token)
    {
        var step = await _coordinators[0].ReadStepAsync(InFlight, Definition(Target, InFlight), "expand", token);
        recorder.InFlightAtomic = !await ColumnAsync(InFlight, token) && step is null or { State: not SchemaDeploymentAttemptState.Completed };
        recorder.Check(recorder.InFlightAtomic, "interrupted step left neither its DDL nor a completed journal record");
    }

    internal override async Task RecoverAsync(CancellationToken token)
    {
        _newLease ??= await _coordinators[0].AcquireAsync(InFlight, Definition(Target, InFlight), "owner-after-fault", Lease, token)
            ?? throw new InvalidOperationException("The interrupted owner's deployment lease has not expired yet.");
        var step = await _coordinators[0].ExecuteTransactionalSqlStepAsync(_newLease, Definition(Target, InFlight), "expand", token);
        FailoverFixture.Check(step.State == SchemaDeploymentAttemptState.Completed, "successor completed the interrupted step");
    }

    internal override async Task VerifyAsync(ScenarioRecorder recorder, CancellationToken token)
    {
        var definition = Definition(Target, InFlight);
        recorder.BeforeFence = _oldLease!.FencingToken;
        recorder.AfterFence = _newLease!.FencingToken;
        recorder.Check(recorder.AfterFence > recorder.BeforeFence, "successor holds a newer fence");
        bool staleRejected = !await _coordinators[0].RenewAsync(_oldLease, definition, Lease, token);
        try
        {
            _ = await _coordinators[0].ExecuteTransactionalSqlStepAsync(_oldLease, definition, "expand", token);
            staleRejected = false;
        }
        catch (InvalidOperationException) { }

        recorder.StaleOwnerRejected = staleRejected;
        recorder.Check(staleRejected, "stale owner cannot renew or run a step");
        recorder.Check(!(await _coordinators[0].ExecuteTransactionalSqlStepAsync(_newLease, definition, "expand", token)).IsNew, "a repeated step retry reads the durable record");
        int verified = 0;
        for (int journal = 0; journal < Journals.Length; journal++)
        {
            for (int index = 0; index < DeploymentsPerJournal; index++)
            {
                string id = Journals[journal] + "-" + Index(index);
                var step = await _coordinators[journal].ReadStepAsync(id, Definition(Target, id), "expand", token);
                if (step is { State: SchemaDeploymentAttemptState.Completed, Attempt: 1 } && await ColumnAsync(id, token)) { verified++; }
            }
        }

        recorder.Verified = verified;
        recorder.Check(verified == DeploymentsPerJournal * Journals.Length, "every acknowledged step stayed completed exactly once with its DDL");
        recorder.InFlightCommitted = await ColumnAsync(InFlight, token) &&
            (await _coordinators[0].ReadStepAsync(InFlight, definition, "expand", token))?.State == SchemaDeploymentAttemptState.Completed;
        recorder.Check(recorder.InFlightCommitted, "the interrupted step completed exactly once after recovery");
        long foreign = await FailoverFixture.ScalarAsync<long>(_fixture.Admin,
            $"SELECT (SELECT count(*) FROM \"{_prefix}_jb\".deployments WHERE deployment_id LIKE 'a-%') + (SELECT count(*) FROM \"{_prefix}_ja\".deployments WHERE deployment_id LIKE 'b-%')", token);
        recorder.NoCrossTenantReads = foreign == 0;
        recorder.Check(recorder.NoCrossTenantReads, "no cross-journal deployments");
        recorder.ExpectedEffects = DeploymentsPerJournal * Journals.Length + 1;
        recorder.ObservedEffects = await FailoverFixture.ScalarAsync<long>(_fixture.Admin,
            $"SELECT count(*) FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid = a.attrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = '{Target}' AND a.attname = 'label' AND NOT a.attisdropped", token);
        long completedRecords = await FailoverFixture.ScalarAsync<long>(_fixture.Admin,
            $"SELECT (SELECT count(*) FROM \"{_prefix}_ja\".step_attempts WHERE step_id = 'expand' AND state = 1) + (SELECT count(*) FROM \"{_prefix}_jb\".step_attempts WHERE step_id = 'expand' AND state = 1)", token);
        recorder.Check(recorder.ObservedEffects == recorder.ExpectedEffects && completedRecords == recorder.ExpectedEffects, "exactly one applied column and one completed record per step");
    }

    internal static async Task RunChildAsync(string role, string[] arguments, CancellationToken token)
    {
        FailoverFixture.Check(role == "workload" && arguments is [var prefix] && prefix.Length == "fo_schema_".Length + 24 &&
            prefix.StartsWith("fo_schema_", StringComparison.Ordinal) && prefix["fo_schema_".Length..].All(char.IsAsciiHexDigitLower), "parent-generated Schema prefix");
        await using var fixture = new FailoverFixture();
        await using var workload = new SchemaFailover(fixture, ScenarioDriver.ChildApplication, arguments[0]);
        _ = await DeployAllAsync(workload._coordinators, workload.Target, static (index, cancellation) => ScenarioDriver.AcknowledgeToParentAsync(index, cancellation), token);
        string owner = "child-" + Guid.NewGuid().ToString("N")[..16];
        var lease = await BeginInFlightAsync(workload._coordinators[0], workload.Target, owner, token);
        Console.WriteLine("LEASE " + owner + " " + lease.FencingToken.ToString(CultureInfo.InvariantCulture));
        await ScenarioDriver.BlockStartAsync(token);
        _ = await workload._coordinators[0].ExecuteTransactionalSqlStepAsync(lease, Definition(workload.Target, InFlight), "expand", token);
        throw new InvalidOperationException("The child unexpectedly completed its parent-blocked step.");
    }

    private static async Task<int> DeployAllAsync(PostgreSqlSchemaDeploymentCoordinator[] coordinators, string target,
        Func<int, CancellationToken, Task> acknowledged, CancellationToken token)
    {
        int count = 0;
        for (int journal = 0; journal < Journals.Length; journal++)
        {
            for (int index = 0; index < DeploymentsPerJournal; index++)
            {
                string id = Journals[journal] + "-" + Index(index);
                var definition = Definition(target, id);
                await coordinators[journal].RegisterAsync(id, definition, token);
                var lease = await coordinators[journal].AcquireAsync(id, definition, "deployer", Lease, token);
                FailoverFixture.Check(lease is not null, "deployment lease");
                await CompleteBaselineAsync(coordinators[journal], lease!, definition, token);
                var step = await coordinators[journal].ExecuteTransactionalSqlStepAsync(lease!, definition, "expand", token);
                FailoverFixture.Check(step is { IsNew: true, State: SchemaDeploymentAttemptState.Completed }, "acknowledged transactional step");
                FailoverFixture.Check(await coordinators[journal].ReleaseAsync(lease!, definition, token), "deployment lease released");
                await acknowledged(count++, token);
            }
        }

        return count;
    }

    private static async Task<SchemaDeploymentLease> BeginInFlightAsync(PostgreSqlSchemaDeploymentCoordinator coordinator, string target, string owner, CancellationToken token)
    {
        var definition = Definition(target, InFlight);
        await coordinator.RegisterAsync(InFlight, definition, token);
        var lease = await coordinator.AcquireAsync(InFlight, definition, owner, Lease, token);
        FailoverFixture.Check(lease is not null, "in-flight deployment lease");
        await CompleteBaselineAsync(coordinator, lease!, definition, token);
        return lease!;
    }

    private static async Task CompleteBaselineAsync(PostgreSqlSchemaDeploymentCoordinator coordinator, SchemaDeploymentLease lease,
        SchemaDeploymentDefinition definition, CancellationToken token)
    {
        var baseline = await coordinator.BeginExternalStepAsync(lease, definition, "verify-baseline", token);
        await coordinator.CompleteExternalStepAsync(lease, definition, "verify-baseline", baseline.Attempt,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("captured baseline " + lease.DeploymentId))), definition.Plan.BeforeFingerprint, token);
    }

    private async Task<bool> ColumnAsync(string id, CancellationToken token) =>
        await FailoverFixture.ScalarAsync<long>(_fixture.Admin,
            $"SELECT count(*) FROM pg_catalog.pg_attribute WHERE attrelid = '\"{Target}\".{Table(id)}'::regclass AND attname = 'label' AND NOT attisdropped", token) == 1;

    private static SchemaDeploymentDefinition Definition(string target, string id)
    {
        var before = new SchemaCatalogSnapshot(new([new(new("app", "items"), "r", false, false, "d", [new("id", 1, "integer", false)])]));
        var after = new SchemaCatalogSnapshot(new([new(new("app", "items"), "r", false, false, "d", [new("id", 1, "integer", false), new("label", 2, "text", true)])]));
        var plan = SchemaDeploymentPlan.Create(before, after, []);
        string sql = id == InFlight
            ? $"ALTER TABLE \"{target}\".{Table(id)} ADD COLUMN label text DEFAULT \"{target}\".failover_barrier()"
            : $"ALTER TABLE \"{target}\".{Table(id)} ADD COLUMN label text";
        return new(plan, plan.Steps.Select(step => new SchemaDeploymentAction(step.Id,
            step.Phase == SchemaDeploymentPhase.Expand ? SchemaDeploymentActionKind.TransactionalSql : SchemaDeploymentActionKind.External,
            step.Phase == SchemaDeploymentPhase.Expand ? sql : "Failover harness attests " + step.Id)));
    }

    private static string Table(string id) => "items_" + id.Replace('-', '_');
    private static string Index(int index) => index.ToString("D2", CultureInfo.InvariantCulture);

    public override async ValueTask DisposeAsync() => await _source.DisposeAsync();
}

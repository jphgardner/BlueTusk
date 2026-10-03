using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Studio;

namespace BlueTusk.Ecosystem.FailoverHarness;

/// <summary>
/// Studio runs inside the application's web host; its PostgreSQL state is the durable audit
/// trail and the cross-replica admission gate. A lost admission connection releases its
/// advisory slots, an audit retry with identical fields is a no-op while a reused identity with
/// different fields is rejected, and a completion-audit failure leaves an uncertain outcome that
/// a restarted host must reconcile (docs/studio/README.md). Each disturbance interrupts an
/// admitted operation while its completion audit insert is blocked and its admission slot is
/// held. Recovery must release the slot to another replica, keep every acknowledged audit
/// record exactly once, reconcile the interrupted completion exactly once and keep scopes apart.
/// </summary>
internal sealed class StudioFailover : FailoverCase
{
    private const int OperationsPerScope = 8;
    private const string InFlightActor = "inflight-actor";
    private static readonly string[] Scopes = ["tenant-a", "tenant-b"];
    private static readonly string Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("SELECT failover")));
    private readonly FailoverFixture _fixture;
    private readonly string _schema;
    private readonly BlueTuskDataSource _source;
    private readonly PostgreSqlStudioAuditSink _audit;
    private readonly PostgreSqlStudioAdmission _admission;
    private Guid _inFlight;

    private StudioFailover(FailoverFixture fixture, string application, string schema)
    {
        _fixture = fixture;
        _schema = schema;
        _source = fixture.CreateSource(application, 6);
        _audit = new PostgreSqlStudioAuditSink(_source, schema);
        _admission = new PostgreSqlStudioAdmission(_source, schema.Replace('_', '-'), 4, 1);
    }

    internal static Task<IReadOnlyList<ScenarioResult>> RunAsync(FailoverFixture fixture, CancellationToken token) =>
        ScenarioDriver.RunAllAsync(fixture, () => new StudioFailover(fixture, ScenarioDriver.Application, "fo_studio_" + Guid.NewGuid().ToString("N")[..24]),
            [FaultKind.BackendTermination, FaultKind.HostProcessKill, FaultKind.PrimaryCrashRestart, FaultKind.StandbyPromotion], token);

    internal override string Family => "Studio";
    internal override string Semantics => "durable-exact-audit; admission-slot-released-with-session; uncertain-completion-reconciled-by-identity";
    internal override int Tenants => Scopes.Length;
    internal override long BarrierKey => 180942166801;
    internal override string[] ChildArguments => [_schema];

    internal override async Task PrepareAsync(CancellationToken token)
    {
        await _audit.InitializeAsync(token);
        // The in-flight completion audit blocks inside its insert transaction while the admission slot is held.
        await FailoverFixture.ExecuteAsync(_fixture.Admin, $"""
            CREATE FUNCTION "{_schema}".failover_barrier() RETURNS trigger LANGUAGE plpgsql AS $function$
            BEGIN PERFORM pg_advisory_xact_lock({BarrierKey.ToString(CultureInfo.InvariantCulture)}); RETURN NEW; END;$function$;
            CREATE TRIGGER failover_barrier BEFORE INSERT ON "{_schema}".studio_audit FOR EACH ROW
                WHEN (NEW.actor_id = '{InFlightActor}' AND NEW.outcome = 'succeeded') EXECUTE FUNCTION "{_schema}".failover_barrier();
            """, token);
    }

    internal override Task<int> AcknowledgeAsync(CancellationToken token) => RunOperationsAsync(_audit, _admission, token, static (_, _) => Task.CompletedTask);

    internal override Task StartInFlightAsync(CancellationToken token)
    {
        _inFlight = Guid.CreateVersion7();
        return RunInFlightAsync(_audit, _admission, _inFlight, token);
    }

    internal override async Task<int> ReadChildAcknowledgementsAsync(ChildProcess child, CancellationToken token)
    {
        int acknowledged = await ScenarioDriver.ReadAcknowledgementsAsync(child, OperationsPerScope * Scopes.Length, token);
        string[] line = (await child.ReadLineAsync(ScenarioDriver.Phase, token)).Split(' ');
        FailoverFixture.Check(line is ["OPERATION", _] && Guid.TryParse(line[1], out _inFlight), "the child reported its in-flight operation");
        return acknowledged;
    }

    internal override async Task CheckRolledBackAsync(ScenarioRecorder recorder, CancellationToken token)
    {
        recorder.InFlightAtomic = await RowsAsync(_inFlight, "started", token) == 1 && await RowsAsync(_inFlight, "succeeded", token) == 0;
        recorder.Check(recorder.InFlightAtomic, "interrupted completion audit left no record and the attempt audit survived");
    }

    /// <summary>Another replica takes the released scope slot and reconciles the uncertain operation by its identity.</summary>
    internal override async Task RecoverAsync(CancellationToken token)
    {
        var replica = new PostgreSqlStudioAdmission(_source, _schema.Replace('_', '-'), 4, 1);
        await using var slot = await replica.TryAcquireAsync(Scopes[0], token)
            ?? throw new InvalidOperationException("The interrupted operation's admission slot is not released yet.");
        await _audit.RecordAsync(Record(_inFlight, "started", InFlightActor, Scopes[0], 0), token);
        await _audit.RecordAsync(Record(_inFlight, "succeeded", InFlightActor, Scopes[0], 1), token);
    }

    internal override async Task VerifyAsync(ScenarioRecorder recorder, CancellationToken token)
    {
        bool conflictRejected = false;
        try { await _audit.RecordAsync(Record(_inFlight, "succeeded", InFlightActor, Scopes[0], 2), token); }
        catch (InvalidOperationException) { conflictRejected = true; }
        recorder.StaleOwnerRejected = conflictRejected;
        recorder.Check(conflictRejected, "a reused operation identity with different fields is rejected");
        await using (var held = await _admission.TryAcquireAsync(Scopes[0], token))
        {
            recorder.Check(held is not null, "the scope slot is available after reconciliation");
            recorder.Check(await _admission.TryAcquireAsync(Scopes[0], token) is null, "the per-scope cap still holds after recovery");
            await using var other = await _admission.TryAcquireAsync(Scopes[1], token);
            recorder.Check(other is not null, "another scope is admitted independently");
        }

        long verified = await FailoverFixture.ScalarAsync<long>(_fixture.Admin, $"""
            SELECT count(*) FROM (SELECT operation_id FROM "{_schema}".studio_audit WHERE actor_id LIKE 'actor-%'
                GROUP BY operation_id HAVING count(*) FILTER (WHERE outcome = 'started' AND returned_rows = 0) = 1
                    AND count(*) FILTER (WHERE outcome = 'succeeded' AND returned_rows = 1) = 1
                    AND count(DISTINCT scope_id) = 1 AND bool_and(query_fingerprint = '{Fingerprint}')) exact
            """, token);
        recorder.Verified = (int)verified;
        recorder.Check(verified == OperationsPerScope * Scopes.Length, "every acknowledged operation kept exactly one attempt and one completion audit");
        long misplaced = await FailoverFixture.ScalarAsync<long>(_fixture.Admin,
            $"SELECT count(*) FROM \"{_schema}\".studio_audit WHERE actor_id LIKE 'actor-%' AND actor_id NOT LIKE 'actor-' || scope_id || '-%'", token);
        recorder.NoCrossTenantReads = misplaced == 0;
        recorder.Check(recorder.NoCrossTenantReads, "no audit record crossed scopes");
        recorder.InFlightCommitted = await RowsAsync(_inFlight, "started", token) == 1 && await RowsAsync(_inFlight, "succeeded", token) == 1;
        recorder.Check(recorder.InFlightCommitted, "the interrupted operation was reconciled exactly once");
        recorder.ExpectedEffects = (OperationsPerScope * Scopes.Length + 1) * 2;
        recorder.ObservedEffects = await FailoverFixture.ScalarAsync<long>(_fixture.Admin, $"SELECT count(*) FROM \"{_schema}\".studio_audit", token);
        recorder.Check(recorder.ObservedEffects == recorder.ExpectedEffects, "no lost or duplicated audit records");
    }

    internal static async Task RunChildAsync(string role, string[] arguments, CancellationToken token)
    {
        FailoverFixture.Check(role == "workload" && arguments is [var schema] && schema.Length == "fo_studio_".Length + 24 &&
            schema.StartsWith("fo_studio_", StringComparison.Ordinal) && schema["fo_studio_".Length..].All(char.IsAsciiHexDigitLower), "parent-generated Studio schema");
        await using var fixture = new FailoverFixture();
        await using var workload = new StudioFailover(fixture, ScenarioDriver.ChildApplication, arguments[0]);
        _ = await RunOperationsAsync(workload._audit, workload._admission, token, static (index, cancellation) => ScenarioDriver.AcknowledgeToParentAsync(index, cancellation));
        var operation = Guid.CreateVersion7();
        Console.WriteLine("OPERATION " + operation.ToString("D"));
        await ScenarioDriver.BlockStartAsync(token);
        await RunInFlightAsync(workload._audit, workload._admission, operation, token);
        throw new InvalidOperationException("The child unexpectedly completed its parent-blocked operation.");
    }

    private static async Task<int> RunOperationsAsync(PostgreSqlStudioAuditSink audit, PostgreSqlStudioAdmission admission, CancellationToken token,
        Func<int, CancellationToken, Task> acknowledged)
    {
        int count = 0;
        for (int index = 0; index < OperationsPerScope; index++)
        {
            foreach (string scope in Scopes)
            {
                await using var slot = await admission.TryAcquireAsync(scope, token);
                FailoverFixture.Check(slot is not null, "an admitted operation");
                var operation = Guid.CreateVersion7();
                string actor = "actor-" + scope + "-" + index.ToString(CultureInfo.InvariantCulture);
                await audit.RecordAsync(Record(operation, "started", actor, scope, 0), token);
                await audit.RecordAsync(Record(operation, "succeeded", actor, scope, 1), token);
                await acknowledged(count++, token);
            }
        }

        return count;
    }

    /// <summary>The documented operation shape: admit, audit the attempt, run, audit the completion, release.</summary>
    private static async Task RunInFlightAsync(PostgreSqlStudioAuditSink audit, PostgreSqlStudioAdmission admission, Guid operation, CancellationToken token)
    {
        await using var slot = await admission.TryAcquireAsync(Scopes[0], token);
        FailoverFixture.Check(slot is not null, "the in-flight operation was admitted");
        await audit.RecordAsync(Record(operation, "started", InFlightActor, Scopes[0], 0), token);
        await audit.RecordAsync(Record(operation, "succeeded", InFlightActor, Scopes[0], 1), token);
    }

    private async Task<long> RowsAsync(Guid operation, string outcome, CancellationToken token) =>
        await FailoverFixture.ScalarAsync<long>(_fixture.Admin,
            $"SELECT count(*) FROM \"{_schema}\".studio_audit WHERE operation_id = '{operation:D}' AND outcome = '{outcome}'", token);

    private static StudioAuditRecord Record(Guid operation, string outcome, string actor, string scope, int rows) =>
        new(operation, actor, Fingerprint, outcome, rows) { ScopeId = scope };

    public override async ValueTask DisposeAsync() => await _source.DisposeAsync();
}

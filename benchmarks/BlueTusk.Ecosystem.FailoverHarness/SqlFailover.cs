using System.Globalization;
using BlueTusk.Data;

namespace BlueTusk.Ecosystem.FailoverHarness;

/// <summary>
/// Sql has no durable state and no failover semantics of its own: it streams typed rows from a
/// caller-owned connection and never commits or retries (docs/sql/README.md). Its gate therefore
/// qualifies restart safety: a typed read interrupted mid-stream must surface a failure rather
/// than a truncated success, every row it did yield must be an exact prefix, the disturbed
/// session must not linger, the generated result contract must still validate on the recovered
/// server, and a retry must return every application-acknowledged row exactly, per tenant.
/// </summary>
internal sealed class SqlFailover : FailoverCase
{
    private const string Schema = "bluetusk_failover_sql";
    private const int RowsPerTenant = 8;
    private static readonly string[] TenantIds = ["tenant-a", "tenant-b"];
    private readonly FailoverFixture _fixture;
    private readonly BlueTuskDataSource _source;
    private readonly List<Queries.ReadLedger.Row> _yielded = [];

    private SqlFailover(FailoverFixture fixture, string application)
    {
        _fixture = fixture;
        _source = fixture.CreateSource(application, 4);
    }

    internal static Task<IReadOnlyList<ScenarioResult>> RunAsync(FailoverFixture fixture, CancellationToken token) =>
        ScenarioDriver.RunAllAsync(fixture, () => new SqlFailover(fixture, ScenarioDriver.Application),
            [FaultKind.BackendTermination, FaultKind.HostProcessKill, FaultKind.PrimaryCrashRestart, FaultKind.StandbyPromotion], token);

    internal override string Family => "Sql";
    internal override string Semantics => "read-only-typed-streaming; no-durable-state; failure-not-truncation; caller-retry";
    internal override int Tenants => TenantIds.Length;
    internal override long BarrierKey => 180942166701;
    internal override string[] ChildArguments => [];

    internal override async Task PrepareAsync(CancellationToken token) =>
        await FailoverFixture.ExecuteAsync(_fixture.Admin, $"""
            DROP SCHEMA IF EXISTS {Schema} CASCADE;
            CREATE SCHEMA {Schema};
            CREATE TABLE {Schema}.ledger(tenant text NOT NULL, id integer NOT NULL, amount bigint NOT NULL, PRIMARY KEY(tenant, id));
            CREATE FUNCTION {Schema}.gate(id integer, armed boolean) RETURNS text LANGUAGE plpgsql VOLATILE AS $function$
            BEGIN IF armed AND id = 5 THEN PERFORM pg_advisory_xact_lock_shared({BarrierKey.ToString(CultureInfo.InvariantCulture)}); END IF; RETURN 'open'; END;$function$;
            """, token);

    internal override async Task<int> AcknowledgeAsync(CancellationToken token)
    {
        int acknowledged = await InsertAllAsync(_source, static (_, _) => Task.CompletedTask, token);
        await Queries.ReadLedger.Definition.ValidateAsync(_source, new(TenantIds[0], false), token);
        return acknowledged;
    }

    internal override async Task StartInFlightAsync(CancellationToken token)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await foreach (var row in Queries.ReadLedger.Definition.ReadAsync(connection, new(TenantIds[0], true), cancellationToken: token))
        {
            _yielded.Add(row);
        }
    }

    internal override Task<int> ReadChildAcknowledgementsAsync(ChildProcess child, CancellationToken token) =>
        ScenarioDriver.ReadAcknowledgementsAsync(child, RowsPerTenant * TenantIds.Length, token);

    internal override async Task CheckRolledBackAsync(ScenarioRecorder recorder, CancellationToken token)
    {
        bool prefix = _yielded.Count < RowsPerTenant && _yielded.Select((row, index) => row == Expected(0, index)).All(static same => same);
        long lingering = await FailoverFixture.ScalarAsync<long>(_fixture.Admin,
            $"SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name IN ('{ScenarioDriver.Application}','{ScenarioDriver.ChildApplication}') AND wait_event_type='Lock'", token);
        recorder.InFlightAtomic = prefix && lingering == 0;
        recorder.Check(recorder.InFlightAtomic, "interrupted read yielded only an exact prefix and left no blocked session");
    }

    internal override async Task RecoverAsync(CancellationToken token) =>
        FailoverFixture.Check((await ReadTenantAsync(0, token)).Count == RowsPerTenant, "a retried read returned the full result");

    internal override async Task VerifyAsync(ScenarioRecorder recorder, CancellationToken token)
    {
        await Queries.ReadLedger.Definition.ValidateAsync(_source, new(TenantIds[1], false), token);
        recorder.Check(true, "the generated result contract validates on the recovered server");
        int verified = 0;
        bool isolated = true;
        long observed = 0;
        for (int tenant = 0; tenant < TenantIds.Length; tenant++)
        {
            var rows = await ReadTenantAsync(tenant, token);
            observed += rows.Count;
            for (int index = 0; index < rows.Count; index++)
            {
                if (index < RowsPerTenant && rows[index] == Expected(tenant, index)) { verified++; }
                isolated &= rows[index].Tenant == TenantIds[tenant];
            }
        }

        recorder.Verified = verified;
        recorder.Check(verified == RowsPerTenant * TenantIds.Length, "every acknowledged row is read back exactly");
        recorder.NoCrossTenantReads = isolated;
        recorder.Check(isolated, "no cross-tenant reads");
        recorder.ExpectedEffects = RowsPerTenant * TenantIds.Length;
        recorder.ObservedEffects = observed;
        recorder.Check(observed == recorder.ExpectedEffects, "no lost or duplicated rows");
    }

    internal static async Task RunChildAsync(string role, string[] arguments, CancellationToken token)
    {
        FailoverFixture.Check(role == "workload" && arguments.Length == 0, "Sql child arguments");
        await using var fixture = new FailoverFixture();
        await using var workload = new SqlFailover(fixture, ScenarioDriver.ChildApplication);
        _ = await InsertAllAsync(workload._source, static (index, cancellation) => ScenarioDriver.AcknowledgeToParentAsync(index, cancellation), token);
        await ScenarioDriver.BlockStartAsync(token);
        await workload.StartInFlightAsync(token);
        throw new InvalidOperationException("The child unexpectedly completed its parent-blocked read.");
    }

    private async Task<List<Queries.ReadLedger.Row>> ReadTenantAsync(int tenant, CancellationToken token)
    {
        var rows = new List<Queries.ReadLedger.Row>();
        await using var connection = await _source.OpenConnectionAsync(token);
        await foreach (var row in Queries.ReadLedger.Definition.ReadAsync(connection, new(TenantIds[tenant], false), cancellationToken: token)) { rows.Add(row); }
        return rows;
    }

    private static async Task<int> InsertAllAsync(BlueTuskDataSource source, Func<int, CancellationToken, Task> acknowledged, CancellationToken token)
    {
        int count = 0;
        for (int tenant = 0; tenant < TenantIds.Length; tenant++)
        {
            for (int index = 0; index < RowsPerTenant; index++)
            {
                var row = Expected(tenant, index);
                await FailoverFixture.ExecuteAsync(source, $"INSERT INTO {Schema}.ledger VALUES('{row.Tenant}',{row.Id.ToString(CultureInfo.InvariantCulture)},{row.Amount.ToString(CultureInfo.InvariantCulture)})", token);
                await acknowledged(count++, token);
            }
        }

        return count;
    }

    private static Queries.ReadLedger.Row Expected(int tenant, int index) => new(TenantIds[tenant], index + 1, (tenant + 1) * 1000L + index, "open");

    public override async ValueTask DisposeAsync() => await _source.DisposeAsync();
}

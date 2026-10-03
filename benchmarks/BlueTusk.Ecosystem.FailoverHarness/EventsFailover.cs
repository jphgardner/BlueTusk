using System.Data.Common;
using System.Globalization;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Events;

namespace BlueTusk.Ecosystem.FailoverHarness;

/// <summary>
/// Events stores appends in the caller's transaction and delivers them at least once; the inbox
/// makes each consumer's database effect exactly once, and a replay lease with a persisted fence
/// keeps the checkpoint across owner replacement and restart (docs/events/README.md). Each
/// disturbance interrupts a replay batch after its first effect is staged and while the batch
/// holds the replay row. Recovery must roll the batch back whole, fence the old owner, resume
/// from the durable checkpoint and finish with exactly one effect per acknowledged event.
/// </summary>
internal sealed class EventsFailover : FailoverCase
{
    private const string Consumer = "failover-effects";
    private const int EventsPerTenant = 8;
    private const int FirstBatch = 3;
    private const string BarrierEventType = "order.barrier";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(5);
    private static readonly string[] TenantIds = ["tenant-a", "tenant-b"];
    private readonly FailoverFixture _fixture;
    private readonly string _schema;
    private readonly string _effects;
    private readonly BlueTuskDataSource _source;
    private readonly PostgreSqlEventStore _store;
    private EventReplayLease? _oldLease;
    private EventReplayLease? _newLease;

    private EventsFailover(FailoverFixture fixture, string application, string schema)
    {
        _fixture = fixture;
        _schema = schema;
        _effects = schema + "_fx";
        _source = fixture.CreateSource(application, 4);
        _store = new PostgreSqlEventStore(_source, new() { Schema = schema });
    }

    internal static Task<IReadOnlyList<ScenarioResult>> RunAsync(FailoverFixture fixture, CancellationToken token) =>
        ScenarioDriver.RunAllAsync(fixture, () => new EventsFailover(fixture, ScenarioDriver.Application, "fo_events_" + Guid.NewGuid().ToString("N")[..24]),
            [FaultKind.BackendTermination, FaultKind.HostProcessKill, FaultKind.PrimaryCrashRestart, FaultKind.StandbyPromotion], token);

    internal override string Family => "Events";
    internal override string Semantics => "acknowledged-append-durable; at-least-once-delivery; exactly-once-inbox-effect; fenced-replay-lease";
    internal override int Tenants => TenantIds.Length;
    internal override long BarrierKey => 180942166301;
    internal override string[] ChildArguments => [_schema];

    internal override async Task PrepareAsync(CancellationToken token)
    {
        await _store.InitializeAsync(token);
        await FailoverFixture.ExecuteAsync(_fixture.Admin, $"""
            CREATE SCHEMA "{_effects}";
            CREATE TABLE "{_effects}".effects(consumer text NOT NULL, tenant text NOT NULL, event_id uuid NOT NULL, sequence bigint NOT NULL, PRIMARY KEY(consumer, tenant, event_id));
            """, token);
    }

    internal override async Task<int> AcknowledgeAsync(CancellationToken token)
    {
        int acknowledged = await AppendAllAsync(_store, _source, token);
        _oldLease = await _store.AcquireReplayAsync(Consumer, Stream(0), "owner-before-fault", Lease, token);
        FailoverFixture.Check(_oldLease is not null && (await _store.ReplayAsync(_oldLease, Handle, FirstBatch, 1 << 20, token)).Checkpoint == FirstBatch,
            "first replay batch committed its checkpoint");
        return acknowledged;
    }

    internal override Task StartInFlightAsync(CancellationToken token) =>
        _store.ReplayAsync(_oldLease!, Handle, EventsPerTenant, 1 << 20, token).AsTask();

    internal override Task<int> ReadChildAcknowledgementsAsync(ChildProcess child, CancellationToken token) => ReadChildAsync(child, token);

    private async Task<int> ReadChildAsync(ChildProcess child, CancellationToken token)
    {
        string[] lease = (await child.ReadLineAsync(ScenarioDriver.Phase, token)).Split(' ');
        FailoverFixture.Check(lease is ["LEASE", _, _], "the child reported its issued replay lease");
        _oldLease = new EventReplayLease(Consumer, Stream(0), lease[1], long.Parse(lease[2], CultureInfo.InvariantCulture));
        return await ScenarioDriver.ReadAcknowledgementsAsync(child, EventsPerTenant * TenantIds.Length, token);
    }

    internal override async Task CheckRolledBackAsync(ScenarioRecorder recorder, CancellationToken token)
    {
        bool atomic = await _store.ReadCheckpointAsync(Consumer, Stream(0), token) == FirstBatch &&
            await EffectsAsync(TenantIds[0], token) == FirstBatch;
        recorder.InFlightAtomic = atomic;
        recorder.Check(atomic, "interrupted replay batch rolled back whole, including its staged effect");
    }

    internal override async Task RecoverAsync(CancellationToken token)
    {
        _newLease ??= await _store.AcquireReplayAsync(Consumer, Stream(0), "owner-after-fault", Lease, token)
            ?? throw new InvalidOperationException("The interrupted owner's replay lease has not expired yet.");
        _ = await _store.ReplayAsync(_newLease, Handle, 1, 1 << 20, token);
    }

    internal override async Task VerifyAsync(ScenarioRecorder recorder, CancellationToken token)
    {
        recorder.BeforeFence = _oldLease!.FencingToken;
        recorder.AfterFence = _newLease!.FencingToken;
        recorder.Check(recorder.AfterFence > recorder.BeforeFence, "replacement owner holds a newer fence");
        bool fenced = false;
        try { _ = await _store.ReplayAsync(_oldLease, Handle, EventsPerTenant, 1 << 20, token); }
        catch (EventReplayFencedException) { fenced = true; }
        recorder.StaleOwnerRejected = fenced && !await _store.RenewReplayAsync(_oldLease, Lease, token);
        recorder.Check(recorder.StaleOwnerRejected, "stale owner cannot replay or renew");
        await DrainAsync(_newLease, token);
        var other = await _store.AcquireReplayAsync(Consumer, Stream(1), "owner-tenant-b", Lease, token);
        FailoverFixture.Check(other is not null, "second tenant replay lease");
        await DrainAsync(other!, token);
        int verified = 0;
        bool isolated = true;
        for (int tenant = 0; tenant < TenantIds.Length; tenant++)
        {
            var stored = await _store.ReadAsync(Stream(tenant), 0, 64, 1 << 20, token);
            var otherIds = (await _store.ReadAsync(Stream(1 - tenant), 0, 64, 1 << 20, token)).Select(static value => value.EventId).ToHashSet();
            for (int index = 0; index < stored.Count; index++)
            {
                if (stored[index].Sequence == index + 1 && stored[index].EventId == EventId(tenant, index) &&
                    Encoding.UTF8.GetString(stored[index].Payload.Span) == Payload(tenant, index)) { verified++; }
                isolated &= !otherIds.Contains(stored[index].EventId);
            }

            recorder.Check(stored.Count == EventsPerTenant, "every acknowledged append is present without gaps");
            recorder.Check(await _store.ReadCheckpointAsync(Consumer, Stream(tenant), token) == EventsPerTenant, "every tenant checkpoint reached its last event");
        }

        recorder.Verified = verified;
        recorder.Check(verified == EventsPerTenant * TenantIds.Length, "every acknowledged event survived with its exact identity, order and payload");
        recorder.NoCrossTenantReads = isolated;
        recorder.Check(isolated, "no cross-tenant reads");
        var receipts = new List<EventAppendReceipt>();
        await using (var connection = await _source.OpenConnectionAsync(token))
        await using (var transaction = await connection.BeginTransactionAsync(token))
        {
            for (int tenant = 0; tenant < TenantIds.Length; tenant++)
            {
                for (int index = 0; index < EventsPerTenant; index++)
                {
                    receipts.AddRange(await _store.AppendAsync(connection, transaction, Stream(tenant), [Write(tenant, index)], token));
                }
            }

            await transaction.CommitAsync(token);
        }

        recorder.Check(receipts.Count == EventsPerTenant * TenantIds.Length && receipts.All(static receipt => receipt.WasAlreadyStored),
            "retried appends are recognised as already stored");
        recorder.InFlightCommitted = await EffectsAsync(TenantIds[0], token) == EventsPerTenant;
        recorder.Check(recorder.InFlightCommitted, "the interrupted batch was redelivered and applied after recovery");
        recorder.ExpectedEffects = EventsPerTenant * TenantIds.Length;
        recorder.ObservedEffects = await FailoverFixture.ScalarAsync<long>(_fixture.Admin, $"SELECT count(*) FROM \"{_effects}\".effects WHERE consumer='{Consumer}'", token);
        recorder.Check(recorder.ObservedEffects == recorder.ExpectedEffects, "exactly one effect per acknowledged event");
    }

    internal static async Task RunChildAsync(string role, string[] arguments, CancellationToken token)
    {
        FailoverFixture.Check(role == "workload" && arguments is [var schema] && schema.Length == "fo_events_".Length + 24 &&
            schema.StartsWith("fo_events_", StringComparison.Ordinal) && schema["fo_events_".Length..].All(char.IsAsciiHexDigitLower), "parent-generated Events schema");
        await using var fixture = new FailoverFixture();
        await using var workload = new EventsFailover(fixture, ScenarioDriver.ChildApplication, arguments[0]);
        string owner = "child-" + Guid.NewGuid().ToString("N");
        var lease = await workload._store.AcquireReplayAsync(Consumer, Stream(0), owner, Lease, token);
        FailoverFixture.Check(lease is not null, "child replay lease");
        Console.WriteLine("LEASE " + owner + " " + lease!.FencingToken.ToString(CultureInfo.InvariantCulture));
        await Console.Out.FlushAsync(token);
        int index = 0;
        for (int tenant = 0; tenant < TenantIds.Length; tenant++)
        {
            for (int sequence = 0; sequence < EventsPerTenant; sequence++)
            {
                await using var connection = await workload._source.OpenConnectionAsync(token);
                await using var transaction = await connection.BeginTransactionAsync(token);
                _ = await workload._store.AppendAsync(connection, transaction, Stream(tenant), [Write(tenant, sequence)], token);
                await transaction.CommitAsync(token);
                await ScenarioDriver.AcknowledgeToParentAsync(index++, token);
            }
        }

        FailoverFixture.Check((await workload._store.ReplayAsync(lease, workload.Handle, FirstBatch, 1 << 20, token)).Checkpoint == FirstBatch, "child first replay batch");
        await ScenarioDriver.BlockStartAsync(token);
        _ = await workload._store.ReplayAsync(lease, workload.Handle, EventsPerTenant, 1 << 20, token);
        throw new InvalidOperationException("The child unexpectedly completed its parent-blocked replay.");
    }

    private async ValueTask Handle(StoredEvent value, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO \"{_effects}\".effects VALUES('{Consumer}',@tenant,@id,@sequence)";
            Add(command, "tenant", value.Stream.TenantId);
            Add(command, "id", value.EventId);
            Add(command, "sequence", value.Sequence);
            _ = await command.ExecuteNonQueryAsync(token);
        }

        // The barrier event blocks after the batch already staged its first effect.
        if (value.EventType == BarrierEventType)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT pg_advisory_xact_lock(" + BarrierKey.ToString(CultureInfo.InvariantCulture) + ")";
            _ = await command.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task<int> AppendAllAsync(PostgreSqlEventStore store, BlueTuskDataSource source, CancellationToken token)
    {
        int acknowledged = 0;
        for (int tenant = 0; tenant < TenantIds.Length; tenant++)
        {
            for (int index = 0; index < EventsPerTenant; index++)
            {
                await using var connection = await source.OpenConnectionAsync(token);
                await using var transaction = await connection.BeginTransactionAsync(token);
                var receipt = await store.AppendAsync(connection, transaction, Stream(tenant), [Write(tenant, index)], token);
                await transaction.CommitAsync(token);
                FailoverFixture.Check(receipt is [{ WasAlreadyStored: false }], "a new acknowledged append");
                acknowledged++;
            }
        }

        return acknowledged;
    }

    private async Task DrainAsync(EventReplayLease lease, CancellationToken token)
    {
        for (int pass = 0; pass < 32; pass++)
        {
            if ((await _store.ReplayAsync(lease, Handle, EventsPerTenant, 1 << 20, token)).ReachedEnd) { return; }
        }

        FailoverFixture.Check(false, "bounded replay reached the end of the stream");
    }

    private async Task<long> EffectsAsync(string tenant, CancellationToken token) =>
        await FailoverFixture.ScalarAsync<long>(_fixture.Admin, $"SELECT count(*) FROM \"{_effects}\".effects WHERE consumer='{Consumer}' AND tenant='{tenant}'", token);

    private static EventStreamKey Stream(int tenant) => new(TenantIds[tenant], "orders");

    // Deterministic identities let the parent verify a killed child's acknowledged appends exactly.
    private static Guid EventId(int tenant, int index) => new(tenant * 1000 + index + 1, 0x4f46, 0x4556, [1, 2, 3, 4, 5, 6, 7, 8]);

    private static string Payload(int tenant, int index) => "{\"tenant\":\"" + TenantIds[tenant] + "\",\"index\":" + index.ToString(CultureInfo.InvariantCulture) + "}";

    private static EventWrite Write(int tenant, int index) =>
        new(EventId(tenant, index), tenant == 0 && index == FirstBatch + 1 ? BarrierEventType : "order.changed", 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Encoding.UTF8.GetBytes(Payload(tenant, index)));

    private static void Add<T>(DbCommand command, string name, T value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    public override async ValueTask DisposeAsync() => await _source.DisposeAsync();
}

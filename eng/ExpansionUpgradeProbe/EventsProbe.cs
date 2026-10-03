using System.Globalization;
using System.Text;
using BlueTusk.Events;
using static BlueTusk.UpgradeProbe.ProbeContext;

namespace BlueTusk.UpgradeProbe;

/// <summary>
/// Events rehearsal across a durable schema migration. The baseline appends three events, replays one
/// and stops holding its replay lease. The candidate must migrate the schema forward, keep every event,
/// deduplicate a retried append, honour the lease until it expires, fence the stale owner and replay the
/// rest exactly once. On rollback the baseline must be refused initialization of the newer schema
/// without changing it (unknown versions are rejected), while its already-initialized writer path still
/// appends with an identity recorded by the database trigger and can replay to the end.
/// </summary>
internal static class FamilyProbe
{
    public const string Family = "Events";
    private const string Consumer = "upgrade-projector";
    private static readonly EventStreamKey Stream = new("upgrade", "orders-1");

    public static async Task RunAsync(ProbeContext context)
    {
        var schema = context.SchemaBase + "_events";
        var probe = context.SchemaBase + "_probe";
        var store = new PostgreSqlEventStore(context.DataSource, new PostgreSqlEventsOptions { Schema = schema });
        await context.FingerprintAsync("before", schema);
        var rejected = 0;
        try
        {
            await store.InitializeAsync();
        }
        catch (InvalidOperationException exception) when (context.IsRollback &&
            exception.Message.StartsWith("Unsupported BlueTusk.Events schema version", StringComparison.Ordinal))
        {
            // README.md: unknown versions are rejected. The verifier requires the schema to be unchanged.
            rejected = 1;
        }

        await context.FingerprintAsync("after", schema);
        context.Observe("SchemaVersion", await context.CountAsync($"SELECT version FROM \"{schema}\".schema_version"));
        var leaseExpiry = $"SELECT expires_at FROM \"{schema}\".replay WHERE consumer_id = '{Consumer}'";

        if (context.IsSeed)
        {
            await context.ExecuteAsync($"CREATE SCHEMA \"{probe}\"; CREATE TABLE \"{probe}\".effects (event_id uuid PRIMARY KEY)");
            context.Observe("AppendedEvents", await AppendAsync(context, store, 1, 2, 3));
            var lease = await store.AcquireReplayAsync(Consumer, Stream, "baseline-replayer", InFlightLease)
                ?? throw new InvalidOperationException("The baseline could not acquire the replay lease.");
            var result = await store.ReplayAsync(lease, Effect(probe), maximumEvents: 1);
            context.Observe("ReplayedEvents", result.HandledCount);
            context.Observe("Checkpoint", await store.ReadCheckpointAsync(Consumer, Stream));
            context.Observe("HeldLeases", await context.CountAsync(
                $"SELECT count(*) FROM \"{schema}\".replay WHERE owner_id = 'baseline-replayer' AND expires_at > clock_timestamp()"));
            context.WriteState(new() { ["fence"] = lease.FencingToken.ToString(CultureInfo.InvariantCulture) });
            return;
        }

        var handoff = context.ReadState();
        if (context.IsUpgrade)
        {
            context.Observe("EventsRead", await RequireEventsAsync(store, 3));
            var retry = await AppendAsync(context, store, 1);
            Require(retry == 0, "A retried append of a stored event was stored again.");
            context.Observe("IdempotentRetries", 1);
            await context.RequireUnexpiredAsync(leaseExpiry, "baseline replay lease");
            Require(await store.AcquireReplayAsync(Consumer, Stream, "candidate-replayer", InFlightLease) is null,
                "The candidate acquired a replay lease the baseline still held.");
            context.Observe("HeldLeaseHonoured", 1);
            await context.WaitForExpiryAsync(leaseExpiry, "baseline replay lease");
            var lease = await store.AcquireReplayAsync(Consumer, Stream, "candidate-replayer", InFlightLease)
                ?? throw new InvalidOperationException("The candidate could not take over the expired replay lease.");
            var staleFence = long.Parse(handoff["fence"], CultureInfo.InvariantCulture);
            Require(lease.FencingToken > staleFence, "The candidate takeover did not advance the replay fence.");
            await RequireRejectedAsync<EventReplayFencedException>(
                async () => await store.ReplayAsync(new EventReplayLease(Consumer, Stream, "baseline-replayer", staleFence), Effect(probe)),
                "The stale baseline replay lease could still checkpoint after the candidate takeover.");
            context.Observe("StaleLeaseRejected", 1);
            var result = await store.ReplayAsync(lease, Effect(probe));
            context.Observe("ReplayedEvents", result.HandledCount);
            context.Observe("Checkpoint", await store.ReadCheckpointAsync(Consumer, Stream));
            context.Observe("EffectsRecorded", await context.CountAsync($"SELECT count(*) FROM \"{probe}\".effects"));
            context.Observe("AppendedEvents", await AppendAsync(context, store, 4));
            Require(await store.ReleaseReplayAsync(lease), "The candidate could not release its replay lease.");
            await RequireNewerFormatRejectedAsync(context);
            return;
        }

        context.Observe("InitializeRejected", rejected);
        context.Observe("AppendedEvents", await AppendAsync(context, store, 5));
        context.Observe("IdentityRecorded", await context.CountAsync(
            $"SELECT count(*) FROM \"{schema}\".event_identities WHERE event_id = '{EventId(5)}'"));
        context.Observe("EventsRead", await RequireEventsAsync(store, 5));
        var rollbackLease = await store.AcquireReplayAsync(Consumer, Stream, "rollback-replayer", InFlightLease)
            ?? throw new InvalidOperationException("The rolled-back binary could not acquire the released replay lease.");
        var rolledBack = await store.ReplayAsync(rollbackLease, Effect(probe));
        context.Observe("ReplayedEvents", rolledBack.HandledCount);
        context.Observe("Checkpoint", await store.ReadCheckpointAsync(Consumer, Stream));
        context.Observe("EffectsRecorded", await context.CountAsync($"SELECT count(*) FROM \"{probe}\".effects"));
        Require(await store.ReleaseReplayAsync(rollbackLease), "The rolled-back binary could not release its replay lease.");
    }

    private static Guid EventId(int number) => new(number, 0x5550, 0x4752, [0x41, 0x44, 0x45, 0, 0, 0, 0, (byte)number]);

    private static EventWrite Write(int number) => new(EventId(number), "order.changed", 1,
        new DateTimeOffset(2026, 10, 1, 0, number, 0, TimeSpan.Zero), Encoding.UTF8.GetBytes($"{{\"number\":{number}}}"));

    private static async Task<int> AppendAsync(ProbeContext context, PostgreSqlEventStore store, params int[] numbers)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var receipts = await store.AppendAsync(connection, transaction, Stream, numbers.Select(Write).ToArray());
        await transaction.CommitAsync();
        Require(receipts.Select(receipt => receipt.Sequence).SequenceEqual(numbers.Select(number => (long)number)),
            "Appended events did not keep their stream positions across the binary boundary.");
        return receipts.Count(receipt => !receipt.WasAlreadyStored);
    }

    private static async Task<int> RequireEventsAsync(PostgreSqlEventStore store, int count)
    {
        var events = await store.ReadAsync(Stream);
        Require(events.Count == count && events.Select((stored, index) =>
            stored.Sequence == index + 1 && stored.EventId == EventId(index + 1) &&
            Encoding.UTF8.GetString(stored.Payload.Span) == $"{{\"number\":{index + 1}}}").All(match => match),
            "Stored events were lost, duplicated or changed across the binary boundary.");
        return events.Count;
    }

    private static EventTransactionHandler Effect(string probe) => async (stored, connection, transaction, cancellationToken) =>
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // A plain insert: a repeated effect violates the primary key and fails the replay.
        command.CommandText = $"INSERT INTO \"{probe}\".effects (event_id) VALUES ('{stored.EventId}')";
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    };

    private static async Task RequireNewerFormatRejectedAsync(ProbeContext context)
    {
        // events/README.md: unknown versions are rejected.
        var future = context.SchemaBase + "_future";
        var store = new PostgreSqlEventStore(context.DataSource, new PostgreSqlEventsOptions { Schema = future });
        await store.InitializeAsync();
        await context.ExecuteAsync($"UPDATE \"{future}\".schema_version SET version = version + 1");
        await RequireRejectedAsync<InvalidOperationException>(async () => await store.InitializeAsync(),
            "The candidate initialized an Events schema version newer than it supports.");
        await context.DropSchemaAsync(future);
        context.Observe("NewerFormatRejected", 1);
    }
}

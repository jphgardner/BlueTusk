using System.Text.Json.Serialization;
using BlueTusk.Data;
using BlueTusk.Events;
using BlueTusk.Events.Streams;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

var contract = new EventContract<SmokeEvent>("smoke.event", 1, SmokeJson.Default.SmokeEvent);
var write = contract.Create(Guid.NewGuid(), new SmokeEvent(42), DateTimeOffset.UtcNow);
var stream = new EventStreamKey("smoke", "stream");
var envelope = new StoredEvent(stream, 1, write.EventId, write.EventType, write.Version, write.OccurredAt, write.Payload);
if (contract.Deserialize(envelope)?.Value != 42)
{
    throw new InvalidOperationException("Source-generated event serialization failed.");
}

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("Events source-generated serialization smoke passed; live database mode was not configured.");
    return;
}

await using var dataSource = BlueTuskDataSource.Create(connectionString);
var schema = "events_aot_" + Guid.NewGuid().ToString("N");
var store = new PostgreSqlEventStore(dataSource, new PostgreSqlEventsOptions { Schema = schema });
try
{
    await store.InitializeAsync();
    await using (var connection = await dataSource.OpenConnectionAsync())
    await using (var transaction = await connection.BeginTransactionAsync())
    {
        await store.AppendAsync(connection, transaction, stream, [write]);
        await transaction.CommitAsync();
    }

    var router = new EventRouterBuilder().Register(contract,
        static (body, _, _, _, _) => body.Value == 42 ? ValueTask.CompletedTask :
            throw new InvalidOperationException("Typed event routing failed.")).Build();
    var lease = await store.AcquireReplayAsync("smoke", stream, "worker", TimeSpan.FromMinutes(1)) ??
        throw new InvalidOperationException("Replay lease acquisition failed.");
    var result = await store.ReplayAsync(lease, router.HandleAsync);
    if (result.HandledCount != 1 || result.Checkpoint != 1 || !result.ReachedEnd ||
        (await store.ReplayAsync(lease, router.HandleAsync)).HandledCount != 0)
    {
        throw new InvalidOperationException("Transactional replay/inbox smoke failed.");
    }
    var health = await store.ReadReplayStatusAsync("smoke", stream);
    if (!health.StreamExists || !health.ReplayExists || !health.IsLeaseActive || health.StreamHead != 1 || health.Checkpoint != 1 || health.RemainingEvents != 0 || health.FencingToken != lease.FencingToken)
    { throw new InvalidOperationException("Native bounded replay health metadata failed."); }

    var table = new ChangeTable(1, schema, "outbox", 'd', new[]
    {
        ("tenant_id", 25u), ("stream_id", 25u), ("sequence", 20u), ("event_id", 2950u),
        ("event_type", 25u), ("version", 23u), ("occurred_at", 1184u), ("payload", 17u)
    }.Select((entry, ordinal) => new ChangeColumn(ordinal, entry.Item1, entry.Item2, -1, ordinal < 3)));
    var row = new ChangeRow(table, new[]
    {
        "smoke", "stream", "1", write.EventId.ToString("D"), write.EventType, "1",
        write.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture), "\\x" + Convert.ToHexStringLower(write.Payload.Span)
    }.Select(static value => ChangeColumnValue.FromValue(System.Text.Encoding.UTF8.GetBytes(value), ChangeValueEncoding.Text)));
    var source = new ChangeSourceIdentity("aot", "smoke", "slot", "publication");
    var position = new BlueTuskLogSequenceNumber(100);
    var insert = new InsertChange(new ChangeId(source, position, 1, 0), row);
    var processor = new PostgreSqlEventDeliveryProcessor(dataSource, store, new EventOutboxChangeDecoder(schema), "aot-remote", source);
    await using (var delivery = ChangeDeliveryTestFactory.CreateCommitted(source, 1, position, [insert]))
    {
        if ((await processor.ProcessAsync(delivery, router.HandleAsync)).HandledEvents != 1)
        {
            throw new InvalidOperationException("Native Streams event delivery did not process the inbox.");
        }
    }

    await using (var duplicate = ChangeDeliveryTestFactory.CreateCommitted(source, 1, position, [insert]))
    {
        if ((await processor.ProcessAsync(duplicate, router.HandleAsync)).HandledEvents != 0)
        {
            throw new InvalidOperationException("Native Streams event delivery did not deduplicate redelivery.");
        }
    }

    Console.WriteLine("Events live transactional outbox, typed routing, inbox, replay and Streams adapter smoke passed.");
}
finally
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
    await command.ExecuteNonQueryAsync();
}

internal sealed record SmokeEvent(int Value);

[JsonSerializable(typeof(SmokeEvent))]
internal sealed partial class SmokeJson : JsonSerializerContext;

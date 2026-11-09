using System.Data.Common;
using BlueTusk.Data;
using BlueTusk.Events;
using BlueTusk.Events.Streams;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using BlueTusk.Streams.Storage.PostgreSql;
using BlueTusk.TypeSystem;

namespace BlueTusk.Samples.RecoverableOutbox;

internal static class Program
{
    private const string Schema = "outbox_recipe";
    private const string Publication = "outbox_recipe_pub";
    private const string Slot = "outbox_recipe_slot";
    private const string Consumer = "order-effects";

    public static async Task<int> Main(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_OUTBOX_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Set BLUETUSK_OUTBOX_CONNECTION_STRING for a disposable development database.");
            return 2;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var events = new PostgreSqlEventStore(dataSource, new PostgreSqlEventsOptions { Schema = Schema });
        var state = new PostgreSqlChangeStreamStateStore(new PostgreSqlStreamsStorageOptions
        {
            ControlDataSource = dataSource,
            ControlSchema = Schema,
        });

        switch (args)
        {
            case ["init"]:
                await InitializeAsync(dataSource, events, state, token);
                return 0;
            case ["publish", var id] when Guid.TryParse(id, out var eventId) && eventId != Guid.Empty:
                await PublishAsync(dataSource, events, eventId, token);
                return 0;
            case ["consume"]:
            case ["consume", "--fail-before-checkpoint"]:
                return await ConsumeAsync(dataSource, events, state, args.Length == 2, token);
            default:
                Console.Error.WriteLine("Use init, publish <stable event GUID>, or consume [--fail-before-checkpoint].");
                return 2;
        }
    }

    private static async Task InitializeAsync(
        BlueTuskDataSource dataSource,
        PostgreSqlEventStore events,
        PostgreSqlChangeStreamStateStore state,
        CancellationToken token)
    {
        await events.InitializeAsync(token);
        await state.InitializeAsync(token);
        await using var command = dataSource.CreateCommand("""
            CREATE TABLE outbox_recipe.orders (id uuid PRIMARY KEY);
            CREATE TABLE outbox_recipe.order_effects (event_id uuid NOT NULL);
            CREATE PUBLICATION outbox_recipe_pub FOR TABLE outbox_recipe.outbox
                WITH (publish = 'insert');
            """);
        await command.ExecuteNonQueryAsync(token);
        await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
            dataSource.CreateDedicatedSessionOptions(), token);
        _ = await replication.CreateReplicationSlotAsync(Slot, temporary: false, cancellationToken: token);
    }

    private static async Task PublishAsync(
        BlueTuskDataSource dataSource,
        PostgreSqlEventStore events,
        Guid eventId,
        CancellationToken token)
    {
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await InsertAsync(connection, transaction,
            "INSERT INTO outbox_recipe.orders VALUES (@id) ON CONFLICT DO NOTHING", eventId, token);
        var stream = new EventStreamKey("demo-tenant", "orders");
        var write = new EventWrite(eventId, "order.placed", 1, DateTimeOffset.UnixEpoch, "{}"u8);
        await events.AppendAsync(connection, transaction, stream, [write], token);
        await transaction.CommitAsync(token);
        Console.WriteLine($"Published {eventId:D}.");
    }

    private static async Task<int> ConsumeAsync(
        BlueTuskDataSource dataSource,
        PostgreSqlEventStore events,
        PostgreSqlChangeStreamStateStore state,
        bool failBeforeCheckpoint,
        CancellationToken token)
    {
        await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
            dataSource.CreateDedicatedSessionOptions(), token);
        var system = await replication.IdentifySystemAsync(token);
        var database = system.DatabaseName ?? throw new InvalidOperationException("Logical replication requires a database.");
        var source = new ChangeSourceIdentity(system.SystemIdentifier, database, Slot, Publication);
        await using var checkpointing = await CheckpointingChangeDeliveryObserver.AcquireAsync(
            state, ChangeStreamStateKey.Create(source, Consumer), Guid.NewGuid().ToString("N"),
            TimeSpan.FromMinutes(1), ChangeStreamCheckpoint.CreateInitial(source, database, "pgoutput", "outbox-recipe-v1"),
            new LogicalReplicationFeedbackSender(replication), token);
        var start = BlueTuskLogSequenceNumber.Zero;
        if (checkpointing.Checkpoint is { } checkpoint)
        {
            start = checkpoint.AcknowledgedCommitPosition;
            _ = await replication.ValidateResumeCheckpointAsync(
                new BlueTuskLogicalReplicationCheckpoint(system.SystemIdentifier, database, Slot, "pgoutput", start), token);
        }

        var observer = new RecipeObserver(checkpointing);
        var options = new BlueTuskPgOutputReplicationOptions
        {
            SlotName = Slot,
            PublicationNames = [Publication],
            StartPosition = start,
        };
        var stream = new PgOutputChangeStream(
            replication.StartReplicationAsync(options, token).DecodePgOutputAsync(cancellationToken: token),
            source,
            observer: observer);
        var processor = new PostgreSqlEventDeliveryProcessor(
            dataSource, events, new EventOutboxChangeDecoder(Schema), Consumer, source);
        try
        {
            await foreach (var delivery in stream.ReadTransactionsAsync(token))
            {
                await using var ownedDelivery = delivery;
                var result = await processor.ProcessAsync(delivery, async (value, connection, transaction, ct) =>
                {
                    await InsertAsync(connection, transaction,
                        "INSERT INTO outbox_recipe.order_effects VALUES (@id)", value.EventId, ct);
                    observer.Interrupt = failBeforeCheckpoint;
                }, token);
                if (result.OutboxEvents > 0)
                {
                    Console.WriteLine($"Events received {result.OutboxEvents}, effects committed {result.HandledEvents}.");
                    return 0;
                }
            }
        }
        catch (SimulatedInterruption)
        {
            Console.Error.WriteLine("Effect committed before the simulated interruption. Run consume again to replay.");
            return 75;
        }

        throw new InvalidOperationException("The replication stream ended before an outbox event was delivered.");
    }

    private static async ValueTask InsertAsync(
        DbConnection connection, DbTransaction transaction, string sql, Guid id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(token);
    }

    private sealed class RecipeObserver(IChangeDeliveryObserver checkpointing) : IChangeDeliveryObserver
    {
        public bool Interrupt { get; set; }

        public ValueTask AcknowledgeAsync(ChangeTransaction transaction, CancellationToken cancellationToken) =>
            Interrupt
                ? ValueTask.FromException(new SimulatedInterruption())
                : checkpointing.AcknowledgeAsync(transaction, cancellationToken);

        public ValueTask NackAsync(ChangeTransaction transaction, Exception? failure, CancellationToken cancellationToken) =>
            checkpointing.NackAsync(transaction, failure, cancellationToken);
    }

    private sealed class SimulatedInterruption : Exception
    {
    }
}

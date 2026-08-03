using System.Data.Common;
using BlueTusk.Data;
using BlueTusk.Live.DependencyInjection;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Live.Tests;

public sealed class PostgreSqlLiveInvalidationStoreTests
{
    private static readonly ChangeSourceIdentity Source =
        new("live-system", "live-database", "live-slot", "public:orders");

    [Fact]
    public async Task Store_deduplicates_transaction_dependencies_and_serves_cursor_ranges()
    {
        var schema = "bluetusk_live_test_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        var store = new PostgreSqlLiveInvalidationStore(new PostgreSqlLiveStoreOptions
        {
            ControlDataSource = dataSource,
            ControlSchema = schema,
        });
        try
        {
            var table = Table("sales", "orders");
            await using var first = Delivery(table, transactionId: 42, position: 100);
            var consumer = new LiveInvalidationConsumer("primary", store);
            await consumer.ConsumeTransactionAsync(first, TestContext.Current.CancellationToken);
            Assert.Equal(ChangeDeliveryState.Acknowledged, first.State);

            var cursor = await store.GetCurrentCursorAsync("primary", TestContext.Current.CancellationToken);
            Assert.Equal(1, cursor.Value);
            Assert.True(await store.HasChangesAsync(
                "primary",
                [new LiveTableDependency("sales", "orders")],
                new LiveInvalidationCursor(0),
                cursor,
                TestContext.Current.CancellationToken));
            Assert.False(await store.HasChangesAsync(
                "primary",
                [new LiveTableDependency("sales", "customers")],
                new LiveInvalidationCursor(0),
                cursor,
                TestContext.Current.CancellationToken));

            await using var redelivery = Delivery(table, transactionId: 42, position: 100);
            await consumer.ConsumeTransactionAsync(redelivery, TestContext.Current.CancellationToken);
            Assert.Equal(cursor, await store.GetCurrentCursorAsync("primary", TestContext.Current.CancellationToken));
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    [Fact]
    public async Task Consumer_persists_before_acknowledging_and_nacks_failures()
    {
        var observer = new TrackingObserver();
        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            10,
            new BlueTuskLogSequenceNumber(10),
            observer: observer);
        var consumer = new LiveInvalidationConsumer("primary", new FailingSink(observer));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await consumer.ConsumeTransactionAsync(delivery, TestContext.Current.CancellationToken));
        Assert.False(observer.Acknowledged);
        Assert.True(observer.AppendedBeforeAcknowledgement);
        Assert.True(observer.Nacked);
    }

    [Fact]
    public async Task Replay_store_is_sequence_fenced_idempotent_and_expiry_aware()
    {
        var schema = "bluetusk_live_replay_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        var store = new PostgreSqlLiveInvalidationStore(new PostgreSqlLiveStoreOptions
        {
            ControlDataSource = dataSource,
            ControlSchema = schema,
            ReplayRetentionWindow = TimeSpan.FromMinutes(5),
        });
        var identity = Identity();
        var events = new[]
        {
            new LiveReplayEvent(1, LiveEventKind.InitialResult, LiveReplayJsonSerializer.ContentType, "one"u8),
            new LiveReplayEvent(2, LiveEventKind.RowUpdated, LiveReplayJsonSerializer.ContentType, "two"u8),
        };
        try
        {
            var request = new LiveReplayAppendRequest(identity, 0, events);
            Assert.Equal(
                LiveReplayAppendStatus.Stored,
                (await store.AppendReplayAsync(request, TestContext.Current.CancellationToken)).Status);
            Assert.Equal(
                LiveReplayAppendStatus.AlreadyStored,
                (await store.AppendReplayAsync(request, TestContext.Current.CancellationToken)).Status);
            Assert.Equal(
                LiveReplayAppendStatus.SequenceConflict,
                (await store.AppendReplayAsync(
                    new LiveReplayAppendRequest(
                        identity,
                        0,
                        [new LiveReplayEvent(1, LiveEventKind.InitialResult, LiveReplayJsonSerializer.ContentType, "different"u8)]),
                    TestContext.Current.CancellationToken)).Status);

            var read = await store.ReadAsync(identity, 0, 10, TestContext.Current.CancellationToken);
            Assert.Equal(LiveReplayReadStatus.Available, read.Status);
            Assert.Equal([1L, 2L], read.Events.Select(item => item.Sequence));
            Assert.All(read.Events, item => Assert.True(LiveReplayJsonSerializer.VerifyIntegrity(item)));

            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    UPDATE "{schema}".live_replay_events
                    SET recorded_at = clock_timestamp() - interval '10 minutes'
                    """;
                _ = await command.ExecuteNonQueryAsync();
            }

            Assert.Equal(2, await store.PruneAsync(TestContext.Current.CancellationToken));
            var expired = await store.ReadAsync(identity, 0, 10, TestContext.Current.CancellationToken);
            Assert.Equal(LiveReplayReadStatus.Expired, expired.Status);
            Assert.Equal(3, expired.FirstAvailableSequence);
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    [Fact]
    public async Task Store_migrates_v1_metadata_and_rejects_future_schema_versions()
    {
        var schema = "bluetusk_live_upgrade_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    CREATE SCHEMA "{schema}";
                    CREATE TABLE "{schema}".live_storage_metadata (
                        singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                        schema_version integer NOT NULL CHECK (schema_version > 0),
                        updated_at timestamptz NOT NULL DEFAULT clock_timestamp());
                    INSERT INTO "{schema}".live_storage_metadata (singleton, schema_version)
                    VALUES (true, 1);
                    """;
                _ = await command.ExecuteNonQueryAsync();
            }

            var upgraded = new PostgreSqlLiveInvalidationStore(new PostgreSqlLiveStoreOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = schema,
            });
            await upgraded.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                PostgreSqlLiveInvalidationStore.CurrentSchemaVersion,
                await ReadSchemaVersionAsync(dataSource, schema));

            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    UPDATE "{schema}".live_storage_metadata
                    SET schema_version = @future
                    WHERE singleton
                    """;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "future";
                parameter.Value = PostgreSqlLiveInvalidationStore.CurrentSchemaVersion + 1;
                command.Parameters.Add(parameter);
                _ = await command.ExecuteNonQueryAsync();
            }

            var future = new PostgreSqlLiveInvalidationStore(new PostgreSqlLiveStoreOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = schema,
            });
            var exception = await Assert.ThrowsAsync<PostgreSqlLiveStoreException>(
                async () => await future.InitializeAsync(TestContext.Current.CancellationToken));
            Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                PostgreSqlLiveInvalidationStore.CurrentSchemaVersion + 1,
                await ReadSchemaVersionAsync(dataSource, schema));
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    private static ChangeTransactionDelivery Delivery(
        ChangeTable table,
        uint transactionId,
        ulong position)
    {
        var id = new ChangeId(Source, new BlueTuskLogSequenceNumber(position), transactionId, 0);
        var columns = new ChangeRow(
            table,
            [ChangeColumnValue.FromValue("1"u8, ChangeValueEncoding.Text)]);
        var typed = new InsertChange<TestRow>(id, new ChangeRow<TestRow>(columns, new TestRow(1), hasValue: true));
        return ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            transactionId,
            new BlueTuskLogSequenceNumber(position),
            [typed]);
    }

    private static ChangeTable Table(string schema, string table) =>
        new(
            1,
            schema,
            table,
            'd',
            [new ChangeColumn(0, "id", 23, -1, IsKey: true)]);

    private static LiveSubscriptionIdentity Identity() =>
        new(
            "database",
            new string('a', 64),
            new string('b', 64),
            "tenant:a",
            "policy:v1",
            50);

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.")
            : connectionString;
    }

    private static async ValueTask<int> ReadSchemaVersionAsync(
        DbDataSource dataSource,
        string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT schema_version FROM \"{schema}\".live_storage_metadata WHERE singleton";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask DropSchemaAsync(DbDataSource dataSource, string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        _ = await command.ExecuteNonQueryAsync();
    }

    private sealed record TestRow(int Id);

    private sealed class TrackingObserver : IChangeDeliveryObserver
    {
        public bool AppendedBeforeAcknowledgement { get; set; }

        public bool Acknowledged { get; private set; }

        public bool Nacked { get; private set; }

        public ValueTask AcknowledgeAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            Acknowledged = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask NackAsync(
            ChangeTransaction transaction,
            Exception? failure,
            CancellationToken cancellationToken = default)
        {
            Nacked = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingSink(TrackingObserver observer) : ILiveInvalidationSink
    {
        public ValueTask<LiveInvalidationCursor> AppendAsync(
            string databaseIdentity,
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            observer.AppendedBeforeAcknowledgement = !observer.Acknowledged;
            throw new InvalidOperationException("Injected append failure.");
        }
    }
}

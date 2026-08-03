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

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.")
            : connectionString;
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

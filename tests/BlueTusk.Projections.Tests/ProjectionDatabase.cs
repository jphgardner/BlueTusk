using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Projections.Tests;

internal sealed class ProjectionDatabase : IAsyncDisposable
{
    private ProjectionDatabase(BlueTuskDataSource dataSource, PostgreSqlProjectionsOptions options)
    {
        DataSource = dataSource;
        Store = new PostgreSqlProjectionStore(dataSource, options);
        Schema = options.Schema;
    }

    public BlueTuskDataSource DataSource { get; }
    public PostgreSqlProjectionStore Store { get; }
    public string Schema { get; }
    public ChangeSourceIdentity Source { get; } = new("test-system", "test-database", "test-slot", "test-publication");
    public static ChangeTable Customers { get; } = Table(1, "customers", "id", "tenant", "name");
    public static ChangeTable Orders { get; } = Table(2, "orders", "id", "tenant", "customer", "amount");

    public static async ValueTask<ProjectionDatabase> CreateAsync(int maximumInvalidations = 100_000)
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var fixture = new ProjectionDatabase(BlueTuskDataSource.Create(connectionString), new PostgreSqlProjectionsOptions
        {
            Schema = "projections_test_" + Guid.NewGuid().ToString("N"),
            MaximumInvalidationsPerTransaction = maximumInvalidations
        });
        try
        {
            await fixture.Store.InitializeAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public ProjectionIdentity Identity(int version = 1) => new("orders", version, "definition-" + version, Source);

    public async ValueTask<(ProjectionLease Lease, OrdersProjection Definition, SnapshotEpoch Epoch)> BeginAsync(int version = 1)
    {
        var identity = Identity(version);
        await Store.RegisterAsync(identity);
        var lease = await Store.AcquireAsync(identity, "worker", TimeSpan.FromMinutes(5)) ?? throw new InvalidOperationException("Lease unavailable.");
        var epoch = SnapshotEpoch.Create(Source, new BlueTuskLogSequenceNumber(100));
        await Store.StartSnapshotAsync(lease, new SnapshotStart(epoch, 2));
        return (lease, new OrdersProjection(identity), epoch);
    }

    public async ValueTask<(ProjectionLease Lease, OrdersProjection Definition)> ReadyAsync(int version = 1, int orderCount = 1)
    {
        var (lease, definition, epoch) = await BeginAsync(version);
        await Store.ApplySnapshotAsync(lease, definition, Batch(epoch, Orders, 0,
            Enumerable.Range(1, orderCount).Select(i => Order(i.ToString(CultureInfo.InvariantCulture), "first", "customer", i * 10m)), true));
        await Store.ApplySnapshotAsync(lease, definition, Batch(epoch, Customers, 0, [Customer("customer", "first", "Alice")], true));
        await Store.CompleteSnapshotAsync(lease, new SnapshotComplete(epoch, orderCount + 1, 2));
        return (lease, definition);
    }

    public static ChangeRow Customer(string id, string tenant, string name) => Row(Customers, id, tenant, name);
    public static ChangeRow Order(string id, string tenant, string customer, decimal amount) => Row(Orders, id, tenant, customer, amount.ToString(CultureInfo.InvariantCulture));

    public static ChangeSnapshotBatch Batch(SnapshotEpoch epoch, ChangeTable table, long sequence, IEnumerable<ChangeRow> rows, bool final) =>
        new(epoch, table, sequence, rows.Select(row => new ChangeSnapshotRow(SnapshotRowId.Create(epoch, table, [row[0]]), row)), final);

    public ChangeTransactionDelivery Delivery(ulong position, params Func<ChangeId, Change>[] changes)
    {
        var lsn = new BlueTuskLogSequenceNumber(position);
        var id = new ChangeId(Source, lsn, (uint)position, 0);
        return ChangeDeliveryTestFactory.CreateCommitted(Source, id.TransactionId, lsn,
            changes.Select((factory, ordinal) => factory(id with { Ordinal = ordinal })));
    }

    public async ValueTask<OrderView?> ReadAsync(string tenant = "first", string id = "1")
    {
        var document = await Store.ReadActiveDocumentAsync("orders", tenant, id);
        return document is null ? null : JsonSerializer.Deserialize(document.Payload.Span, ProjectionJson.Default.OrderView);
    }

    public async ValueTask<decimal> AggregateAsync(int version = 1, string tenant = "first")
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM \"{Schema}\".aggregates WHERE projection='orders' AND version=@version AND tenant_id=@tenant AND metric='total'";
        command.Parameters.Add(new BlueTuskParameter<int>(version) { ParameterName = "version" });
        command.Parameters.Add(new BlueTuskParameter<string>(tenant) { ParameterName = "tenant" });
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? reader.GetDecimal(0) : 0;
    }

    public async ValueTask<long> CountAsync(string table)
    {
        if (table is not "documents" and not "dependencies" and not "source_rows" and not "snapshot_batches")
        {
            throw new ArgumentException("Unknown fixture table.", nameof(table));
        }

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{Schema}\".{table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE";
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await DataSource.DisposeAsync();
        }
    }

    private static ChangeTable Table(uint oid, string name, params string[] columns) => new(oid, "public", name, 'f',
        columns.Select((column, ordinal) => new ChangeColumn(ordinal, column, 25, -1, ordinal == 0)));

    private static ChangeRow Row(ChangeTable table, params string[] values) => new(table,
        values.Select(static value => ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes(value), ChangeValueEncoding.Text)));
}

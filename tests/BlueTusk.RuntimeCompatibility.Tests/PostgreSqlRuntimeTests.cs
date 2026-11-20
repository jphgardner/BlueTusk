using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.RuntimeCompatibility.Tests;

[Trait("Category", "PostgreSqlCompatibility")]
public sealed class PostgreSqlRuntimeTests
{
    [Fact]
    public async Task Prepared_parameters_preserve_uuid_date_and_decimal_values()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT $1::uuid, $2::date, $3::numeric, $4::int4 + 1";
        var identifier = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var date = new DateOnly(2026, 11, 14);
        const decimal number = 12345678901234567890.12345678m;
        command.Parameters.Add(new BlueTuskParameter<Guid>(identifier));
        command.Parameters.Add(new BlueTuskParameter<DateOnly>(date));
        command.Parameters.Add(new BlueTuskParameter<decimal>(number));
        command.Parameters.Add(new BlueTuskParameter<int>(41));
        await command.PrepareAsync();
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(identifier, reader.GetFieldValue<Guid>(0));
        Assert.Equal(date, reader.GetFieldValue<DateOnly>(1));
        Assert.Equal(number, reader.GetFieldValue<decimal>(2));
        Assert.Equal(42, reader.GetInt32(3));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Native_batch_returns_each_result_set()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync();
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add("SELECT 41::int4");
        batch.BatchCommands.Add("SELECT 42::int4");
        await using var reader = await batch.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(41, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(42, reader.GetInt32(0));
        Assert.False(await reader.NextResultAsync());
    }

    [Fact]
    public async Task Binary_copy_preserves_uuid_byte_order_and_unicode()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync();
        await using var create = connection.CreateCommand();
        create.CommandText = "CREATE TEMP TABLE runtime_copy (id uuid, value text)";
        await create.ExecuteNonQueryAsync();
        var identifier = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        await using (var importer = await connection.BeginBinaryImportAsync("COPY runtime_copy FROM STDIN (FORMAT BINARY)"))
        {
            await importer.StartRowAsync();
            await importer.WriteAsync(identifier);
            await importer.WriteAsync("BlueTusk 🐘");
            Assert.Equal(1, await importer.CompleteAsync());
        }

        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT id, value FROM runtime_copy";
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(identifier, reader.GetGuid(0));
        Assert.Equal("BlueTusk 🐘", reader.GetString(1));
    }

    [Fact]
    public async Task Pool_serves_concurrent_connections_and_remains_reusable()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        var answers = await Task.WhenAll(Enumerable.Range(0, 12).Select(async value =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT $1::int4";
            command.Parameters.Add(new BlueTuskParameter<int>(value));
            return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }));
        Assert.Equal(Enumerable.Range(0, 12), answers);
        await using var reused = dataSource.CreateCommand("SELECT 42::int4");
        Assert.Equal(42, await reused.ExecuteScalarAsync<int>());
    }

    [Fact]
    public async Task Canceled_command_recovers_the_same_session()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync();
        await using var slow = connection.CreateCommand();
        slow.CommandText = "SELECT pg_sleep(10)";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.ExecuteScalarAsync(cancellation.Token));
        await using var next = connection.CreateCommand();
        next.CommandText = "SELECT 42::int4";
        Assert.Equal(42, Convert.ToInt32(await next.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Synchronous_connection_and_transaction_work()
    {
        using var connection = new BlueTuskConnection(GetConnectionString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 42::int4";
        Assert.Equal(42, Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        transaction.Rollback();
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } value
            ? value
            : throw SkipException.ForSkip("A PostgreSQL compatibility fixture is required.");
}

using System.Data;
using System.Globalization;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Sql.Tests;

public sealed class SqlByteAdmissionTests
{
    [Fact]
    public async Task Oversized_fields_are_rejected_before_typed_materialization()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var materialized = false;
        var query = new SqlQuery<int, string>("bounded", "SELECT repeat('x',17) AS value", [new("value", "text", false)],
            static (_, _) => { }, reader => { materialized = true; return reader.GetString(0); }, maximumFieldBytes: 16);
        await Assert.ThrowsAsync<SqlResultLimitException>(async () =>
        {
            await foreach (var _ in query.ReadAsync(connection, 0, cancellationToken: TestContext.Current.CancellationToken)) { }
        });
        Assert.False(materialized);
        await using var healthy = connection.CreateCommand();
        healthy.CommandText = "SELECT 42";
        Assert.Equal(42, await healthy.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Total_encoded_bytes_are_bounded_across_rows()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var query = new SqlQuery<int, string>("bounded", "SELECT repeat('x',8) AS value FROM generate_series(1,3)", [new("value", "text", false)],
            static (_, _) => { }, static reader => reader.GetString(0), maximumFieldBytes: 8, maximumResultBytes: 16);
        var rows = 0;
        await Assert.ThrowsAsync<SqlResultLimitException>(async () =>
        {
            await foreach (var _ in query.ReadAsync(connection, 0, cancellationToken: TestContext.Current.CancellationToken)) { rows++; }
        });
        Assert.Equal(1, rows);
    }

    [Theory]
    [InlineData(CommandBehavior.Default)]
    [InlineData(CommandBehavior.SequentialAccess)]
    public async Task Provider_field_lengths_preserve_null_and_encoded_bytes_without_consuming_the_value(CommandBehavior behavior)
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT NULL::text, '表'::text, decode('000180ff','hex')::bytea, '{\"a\":1}'::jsonb, 42::int4";
        await using var reader = (BlueTuskDataReader)await command.ExecuteReaderAsync(behavior, TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Null(reader.GetFieldByteLength(0));
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(3, reader.GetFieldByteLength(1));
        Assert.Equal("表", reader.GetString(1));
        Assert.Equal(behavior == CommandBehavior.Default ? 10 : 4, reader.GetFieldByteLength(2));
        Assert.Equal("000180FF", Convert.ToHexString(reader.GetFieldValue<byte[]>(2)));
        Assert.Equal(behavior == CommandBehavior.Default ? 8 : 9, reader.GetFieldByteLength(3)); // binary jsonb includes a version byte
        Assert.Equal("{\"a\": 1}", reader.GetString(3));
        Assert.Equal(behavior == CommandBehavior.Default ? 2 : 4, reader.GetFieldByteLength(4));
        Assert.Equal(42, reader.GetInt32(4));
    }

    [Fact]
    public void Column_enumeration_and_null_members_are_bounded_before_materialization()
    {
        Assert.Throws<ArgumentException>(() => new SqlQuery<int, int>("invalid", "SELECT 1", [null!],
            static (_, _) => { }, static reader => reader.GetInt32(0)));
        var visits = 0;
        IEnumerable<SqlResultColumn> Columns()
        {
            while (true) { visits++; yield return new("c" + visits.ToString(CultureInfo.InvariantCulture), "int4", false); }
        }
        Assert.Throws<ArgumentException>(() => new SqlQuery<int, int>("invalid", "SELECT 1", Columns(),
            static (_, _) => { }, static reader => reader.GetInt32(0)));
        Assert.Equal(1025, visits);
    }

    private static string ConnectionString() => Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } connection
        ? connection : throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is required for live PostgreSQL evidence.");
}

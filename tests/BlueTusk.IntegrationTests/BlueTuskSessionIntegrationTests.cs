using System.Buffers.Binary;
using System.Data;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskSessionIntegrationTests
{
    [Fact]
    public async Task Opens_with_scram_and_executes_a_simple_query()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("$XunitDynamicSkip$BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString);
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            },
            CancellationToken.None);

        var result = await session.ExecuteSimpleQueryAsync(
            "SELECT 42::int4 AS answer, 'hello'::text AS greeting, NULL::text AS missing",
            CancellationToken.None);

        var resultSet = Assert.Single(result.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        Assert.Equal(["answer", "greeting", "missing"], resultSet.Fields.Select(field => field.Name));
        Assert.Equal("42", Encoding.UTF8.GetString(row.Values[0]!.Value.Span));
        Assert.Equal("hello", Encoding.UTF8.GetString(row.Values[1]!.Value.Span));
        Assert.Null(row.Values[2]);
        Assert.Equal("SELECT 1", resultSet.CommandTag);
        Assert.Contains("server_version", session.Parameters);
        Assert.NotNull(session.BackendKeyData);
    }

    [Fact]
    public async Task Session_executes_an_extended_query_with_binary_parameters()
    {
        var connectionString = GetConnectionString();
        var settings = new BlueTuskConnectionStringBuilder(connectionString);
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            },
            CancellationToken.None);
        var left = new byte[sizeof(int)];
        var right = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(left, 20);
        BinaryPrimitives.WriteInt32BigEndian(right, 22);

        var result = await session.ExecuteExtendedQueryAsync(
            "SELECT $1::int4 + $2::int4 AS answer",
            [
                new BlueTuskExtendedQueryParameter(23, 1, left),
                new BlueTuskExtendedQueryParameter(23, 1, right),
            ],
            CancellationToken.None);

        var resultSet = Assert.Single(result.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        Assert.Equal("42", Encoding.UTF8.GetString(row.Values[0]!.Value.Span));
    }

    [Fact]
    public async Task Session_cancels_and_drains_before_reuse()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString());
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            },
            CancellationToken.None);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.ExecuteSimpleQueryAsync("SELECT pg_sleep(10)", cancellationSource.Token).AsTask());

        var result = await session.ExecuteSimpleQueryAsync("SELECT 42::int4", CancellationToken.None);
        var row = Assert.Single(Assert.Single(result.ResultSets).Rows);
        Assert.Equal("42", Encoding.UTF8.GetString(row.Values[0]!.Value.Span));
    }

    [Fact]
    public async Task AdoNet_connection_command_and_reader_execute_end_to_end()
    {
        var connectionString = GetConnectionString();
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new BlueTuskCommand(
            "SELECT 42::int4 AS answer, 'hello'::text AS greeting, NULL::text AS missing, current_timestamp AS now, point(1, 2) AS unknown_value; SELECT 7::int4 AS second",
            connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(42, reader.GetInt32(reader.GetOrdinal("answer")));
        Assert.Equal("hello", reader.GetString(1));
        Assert.True(await reader.IsDBNullAsync(2, CancellationToken.None));
        Assert.IsType<DateTimeOffset>(reader.GetValue(3));
        Assert.Equal("(1,2)", reader.GetFieldValue<BlueTuskUnknownValue>(4).GetText());
        Assert.False(await reader.ReadAsync(CancellationToken.None));
        Assert.True(await reader.NextResultAsync(CancellationToken.None));
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(7, reader.GetInt32(0));
        Assert.False(await reader.NextResultAsync(CancellationToken.None));
        Assert.NotEmpty(connection.ServerVersion);
    }

    [Fact]
    public async Task AdoNet_typed_scalar_decodes_int4()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new BlueTuskCommand("SELECT 6::int4 * 7::int4", connection);

        Assert.Equal(42, await command.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task AdoNet_parameters_execute_through_the_extended_protocol()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand("SELECT $1::int4 + $2::int4");
        command.Parameters.Add(new BlueTuskParameter<int>(20));
        command.Parameters.Add(new BlueTuskParameter<int>(22));

        Assert.Equal(42, await command.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task AdoNet_parameter_values_are_not_interpolated_into_sql()
    {
        const string injectionShapedValue = "'; DROP TABLE important_data; --";
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand("SELECT $1::text");
        command.Parameters.Add(new BlueTuskParameter<string>(injectionShapedValue));

        Assert.Equal(injectionShapedValue, await command.ExecuteScalarAsync<string>(CancellationToken.None));
    }

    [Fact]
    public async Task AdoNet_null_parameters_use_the_declared_type()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand("SELECT $1::text IS NULL");
        command.Parameters.Add(new BlueTuskParameter(DBNull.Value) { DbType = DbType.String });

        Assert.True(await command.ExecuteScalarAsync<bool>(CancellationToken.None));
    }

    [Fact]
    public async Task Data_source_commands_own_their_physical_connection()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand("SELECT 40::int4 + 2::int4");

        Assert.Equal(42, await command.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task Errors_are_drained_through_ready_for_query()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var invalid = new BlueTuskCommand("SELEC broken", connection);

        var exception = await Assert.ThrowsAsync<BlueTuskException>(
            () => invalid.ExecuteNonQueryAsync(CancellationToken.None));

        Assert.Equal("42601", exception.SqlState);
        await using var valid = new BlueTuskCommand("SELECT 1::int4", connection);
        Assert.Equal(1, await valid.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task Extended_query_errors_are_drained_through_ready_for_query()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var invalid = new BlueTuskCommand("SELECT $1::int4", connection);
        invalid.Parameters.Add(new BlueTuskParameter<string>("not-an-integer"));

        var exception = await Assert.ThrowsAsync<BlueTuskException>(
            () => invalid.ExecuteNonQueryAsync(CancellationToken.None));

        Assert.Equal("22P02", exception.SqlState);
        await using var valid = new BlueTuskCommand("SELECT $1::int4", connection);
        valid.Parameters.Add(new BlueTuskParameter<int>(42));
        Assert.Equal(42, await valid.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("$XunitDynamicSkip$BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }
}

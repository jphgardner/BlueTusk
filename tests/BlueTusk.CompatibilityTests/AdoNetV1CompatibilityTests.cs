using System.Data;
using BlueTusk.Client;
using BlueTusk.Data;
using Dapper;
using Xunit.Sdk;

namespace BlueTusk.CompatibilityTests;

public sealed class AdoNetV1CompatibilityTests
{
    [Fact]
    public async Task Dapper_executes_parameters_and_materializes_pocos()
    {
        await using var connection = await OpenAsync();

        var row = await connection.QuerySingleAsync<DapperRow>(
            "SELECT @number::int4 AS Number, @label::text AS Label",
            new { number = 42, label = "BlueTusk" });

        Assert.Equal(42, row.Number);
        Assert.Equal("BlueTusk", row.Label);
    }

    [Fact]
    public async Task Text_commands_support_function_in_and_inout_values_as_result_rows()
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            """
            CREATE OR REPLACE FUNCTION pg_temp.bluetusk_add(
                increment int,
                INOUT total int)
            LANGUAGE sql
            AS 'SELECT increment + total'
            """);

        var total = await connection.QuerySingleAsync<int>(
            "SELECT * FROM pg_temp.bluetusk_add(@increment, @total)",
            new { increment = 7, total = 35 });

        Assert.Equal(42, total);
    }

    [Fact]
    public async Task Text_commands_support_procedure_and_function_out_values_as_result_rows()
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            """
            CREATE OR REPLACE PROCEDURE pg_temp.bluetusk_accumulate(
                increment int,
                INOUT total int)
            LANGUAGE plpgsql
            AS $$
            BEGIN
                total := total + increment;
            END
            $$
            """);
        await connection.ExecuteAsync(
            """
            CREATE OR REPLACE FUNCTION pg_temp.bluetusk_expand(
                input int,
                OUT original int,
                OUT doubled int)
            LANGUAGE sql
            AS 'SELECT input, input * 2'
            """);

        var procedureTotal = await connection.QuerySingleAsync<int>(
            "CALL pg_temp.bluetusk_accumulate(@increment, @total)",
            new { increment = 7, total = 35 });
        var functionResult = await connection.QuerySingleAsync<FunctionOutRow>(
            "SELECT * FROM pg_temp.bluetusk_expand(@input)",
            new { input = 21 });

        Assert.Equal(42, procedureTotal);
        Assert.Equal(21, functionResult.Original);
        Assert.Equal(42, functionResult.Doubled);
    }

    [Fact]
    public async Task Reader_behavior_flags_enforce_single_row_single_result_and_close_connection()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT generate_series(1, 3); SELECT 4";

        await using (var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow |
            CommandBehavior.SingleResult))
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync());
            Assert.False(await reader.NextResultAsync());
        }

        await using var sequential = connection.CreateCommand();
        sequential.CommandText = "SELECT generate_series(1, 3)";
        await using (var reader = await sequential.ExecuteReaderAsync(
            CommandBehavior.SingleRow |
            CommandBehavior.SingleResult |
            CommandBehavior.SequentialAccess |
            CommandBehavior.CloseConnection))
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync());
            Assert.False(await reader.NextResultAsync());
        }

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Schema_collections_and_column_schema_are_available()
    {
        await using var connection = await OpenAsync();

        var collections = await connection.GetSchemaAsync();
        var tables = await connection.GetSchemaAsync(
            "Tables",
            [connection.Database, "pg_catalog", "pg_type", "BASE TABLE"]);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1::int4 AS number, 'value'::text AS label";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = await reader.GetColumnSchemaAsync();
        var schemaTable = await reader.GetSchemaTableAsync();

        Assert.Contains(
            collections.Rows.Cast<DataRow>(),
            row => string.Equals(
                row["CollectionName"] as string,
                "Tables",
                StringComparison.Ordinal));
        Assert.NotEmpty(tables.Rows);
        Assert.Collection(
            columns,
            column =>
            {
                Assert.Equal("number", column.ColumnName);
                Assert.Equal(typeof(int), column.DataType);
            },
            column =>
            {
                Assert.Equal("label", column.ColumnName);
                Assert.Equal(typeof(string), column.DataType);
            });
        Assert.NotNull(schemaTable);
        Assert.Equal(2, schemaTable.Rows.Count);
    }

    [Fact]
    public void Unsupported_ado_net_modes_fail_explicitly()
    {
        using var connection = new BlueTuskConnection(
            "Host=localhost;Database=app;Username=app");
        using var command = connection.CreateCommand();
        var parameter = command.CreateParameter();

        Assert.Throws<NotSupportedException>(
            () => command.CommandType = CommandType.StoredProcedure);
        Assert.Throws<NotSupportedException>(
            () => parameter.Direction = ParameterDirection.Output);
        Assert.Throws<NotSupportedException>(
            () => parameter.Direction = ParameterDirection.InputOutput);
        Assert.Throws<NotSupportedException>(
            () => command.ExecuteReader(CommandBehavior.SchemaOnly));
        Assert.Throws<NotSupportedException>(
            () => command.ExecuteReader(CommandBehavior.KeyInfo));
    }

    private static async ValueTask<BlueTuskConnection> OpenAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        var connection = new BlueTuskConnection(settings.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        return connection;
    }

    private sealed class DapperRow
    {
        public int Number { get; init; }

        public string Label { get; init; } = string.Empty;
    }

    private sealed class FunctionOutRow
    {
        public int Original { get; init; }

        public int Doubled { get; init; }
    }
}

using System.Data;
using System.Data.Common;
using System.Globalization;
using BlueTusk.Client;
using BlueTusk.Data;
using Npgsql;
using Xunit.Sdk;

namespace BlueTusk.CompatibilityTests;

public sealed class ProviderDifferentialTests
{
    [Fact]
    public async Task Scalar_and_array_result_values_match_npgsql()
    {
        const string sql =
            "SELECT true::bool, (-123)::int2, 42::int4, 922337203685477580::int8, " +
            "1.25::float4, 2.5::float8, 12345.6789::numeric, " +
            "'00112233-4455-6677-8899-aabbccddeeff'::uuid, " +
            "decode('000102ff', 'hex')::bytea, 'BlueTusk 🦣'::text, " +
            "'{\"answer\":42}'::json, '{\"answer\":42}'::jsonb, ARRAY[1,2,3]::int4[]";
        await using var blueTusk = await OpenBlueTuskAsync();
        await using var npgsql = await OpenNpgsqlAsync();

        var blueTuskValues = await ReadNormalizedRowAsync(blueTusk, sql);
        var npgsqlValues = await ReadNormalizedRowAsync(npgsql, sql);

        Assert.Equal(npgsqlValues, blueTuskValues);
    }

    [Fact]
    public async Task Parameter_round_trips_match_npgsql()
    {
        await using var blueTusk = await OpenBlueTuskAsync();
        await using var npgsql = await OpenNpgsqlAsync();
        var id = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
        byte[] payload = [0, 1, 2, 254, 255];

        await using var blueTuskCommand = new BlueTuskCommand(
            "SELECT @number::int4, @text::text, @id::uuid, @payload::bytea",
            blueTusk);
        blueTuskCommand.Parameters.Add(new BlueTuskParameter<int>(42) { ParameterName = "number" });
        blueTuskCommand.Parameters.Add(new BlueTuskParameter<string>("parameter 🦣") { ParameterName = "text" });
        blueTuskCommand.Parameters.Add(new BlueTuskParameter<Guid>(id) { ParameterName = "id" });
        blueTuskCommand.Parameters.Add(new BlueTuskParameter<byte[]>(payload) { ParameterName = "payload" });

        await using var npgsqlCommand = new NpgsqlCommand(
            "SELECT @number::int4, @text::text, @id::uuid, @payload::bytea",
            npgsql);
        npgsqlCommand.Parameters.AddWithValue("number", 42);
        npgsqlCommand.Parameters.AddWithValue("text", "parameter 🦣");
        npgsqlCommand.Parameters.AddWithValue("id", id);
        npgsqlCommand.Parameters.AddWithValue("payload", payload);

        Assert.Equal(
            await ReadNormalizedRowAsync(npgsqlCommand),
            await ReadNormalizedRowAsync(blueTuskCommand));
    }

    [Fact]
    public async Task Error_and_transaction_state_match_npgsql()
    {
        await using var blueTusk = await OpenBlueTuskAsync();
        await using var npgsql = await OpenNpgsqlAsync();

        var blueTuskStates = await CaptureTransactionFailureStatesAsync(blueTusk);
        var npgsqlStates = await CaptureTransactionFailureStatesAsync(npgsql);

        Assert.Equal(["22012", "25P02", "1"], blueTuskStates);
        Assert.Equal(npgsqlStates, blueTuskStates);
    }

    [Fact]
    public async Task Cancellation_and_connection_reuse_match_npgsql()
    {
        await using var blueTusk = await OpenBlueTuskAsync();
        await using var npgsql = await OpenNpgsqlAsync();

        Assert.Equal("1", await CancelAndReuseAsync(blueTusk));
        Assert.Equal("1", await CancelAndReuseAsync(npgsql));
    }

    [Fact]
    public async Task Reader_schema_metadata_matches_npgsql_for_core_types()
    {
        const string sql =
            "SELECT 1::int4 AS number, 'text'::text AS label, " +
            "decode('abcd', 'hex')::bytea AS payload, true::bool AS enabled";
        await using var blueTusk = await OpenBlueTuskAsync();
        await using var npgsql = await OpenNpgsqlAsync();

        Assert.Equal(
            await ReadSchemaAsync(npgsql, sql),
            await ReadSchemaAsync(blueTusk, sql));
    }

    private static async Task<string[]> ReadNormalizedRowAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await ReadNormalizedRowAsync(command);
    }

    private static async Task<string[]> ReadNormalizedRowAsync(DbCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        var values = new string[reader.FieldCount];
        for (var ordinal = 0; ordinal < values.Length; ordinal++)
        {
            values[ordinal] = Normalize(reader.GetValue(ordinal));
        }

        Assert.False(await reader.ReadAsync(CancellationToken.None));
        return values;
    }

    private static string Normalize(object value) => value switch
    {
        DBNull => "NULL",
        byte[] bytes => Convert.ToHexString(bytes),
        Array array => $"[{string.Join(',', array.Cast<object>().Select(Normalize))}]",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private static async Task<string[]> CaptureTransactionFailureStatesAsync(DbConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        var states = new List<string>();
        await using (var divide = connection.CreateCommand())
        {
            divide.Transaction = transaction;
            divide.CommandText = "SELECT 1 / 0";
            var exception = await Assert.ThrowsAnyAsync<DbException>(
                () => divide.ExecuteScalarAsync(CancellationToken.None));
            states.Add(exception.SqlState ?? string.Empty);
        }

        await using (var failed = connection.CreateCommand())
        {
            failed.Transaction = transaction;
            failed.CommandText = "SELECT 1";
            var exception = await Assert.ThrowsAnyAsync<DbException>(
                () => failed.ExecuteScalarAsync(CancellationToken.None));
            states.Add(exception.SqlState ?? string.Empty);
        }

        await transaction.RollbackAsync(CancellationToken.None);
        await using var valid = connection.CreateCommand();
        valid.CommandText = "SELECT 1";
        states.Add(Normalize((await valid.ExecuteScalarAsync(CancellationToken.None))!));
        return states.ToArray();
    }

    private static async Task<string> CancelAndReuseAsync(DbConnection connection)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT pg_sleep(5)";
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => command.ExecuteNonQueryAsync(cancellation.Token));
        }

        await using var valid = connection.CreateCommand();
        valid.CommandText = "SELECT 1";
        return Normalize((await valid.ExecuteScalarAsync(CancellationToken.None))!);
    }

    private static async Task<string[]> ReadSchemaAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var schema = new string[reader.FieldCount];
        for (var ordinal = 0; ordinal < schema.Length; ordinal++)
        {
            schema[ordinal] = string.Join(
                '|',
                reader.GetName(ordinal),
                NormalizeTypeName(reader.GetDataTypeName(ordinal)),
                reader.GetFieldType(ordinal).FullName);
        }

        return schema;
    }

    private static string NormalizeTypeName(string name) => name switch
    {
        "boolean" => "bool",
        "smallint" => "int2",
        "integer" => "int4",
        "bigint" => "int8",
        "real" => "float4",
        "double precision" => "float8",
        "character" => "bpchar",
        "character varying" => "varchar",
        _ => name,
    };

    private static async ValueTask<BlueTuskConnection> OpenBlueTuskAsync()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString())
        {
            Pooling = false,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        var connection = new BlueTuskConnection(settings.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        return connection;
    }

    private static async ValueTask<NpgsqlConnection> OpenNpgsqlAsync()
    {
        var settings = new NpgsqlConnectionStringBuilder(GetConnectionString())
        {
            Pooling = false,
            SslMode = SslMode.Disable,
        };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        return connection;
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return connectionString;
    }
}

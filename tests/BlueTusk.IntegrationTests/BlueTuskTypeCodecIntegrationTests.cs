using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskTypeCodecIntegrationTests
{
    [Fact]
    public async Task Extended_queries_negotiate_binary_result_columns()
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

        var result = await session.ExecuteExtendedQueryAsync(
            "SELECT $1::bool AS flag, 42::int4 AS answer",
            [new BlueTuskExtendedQueryParameter(16, 1, new byte[] { 1 })],
            CancellationToken.None);

        var resultSet = Assert.Single(result.ResultSets);
        Assert.All(resultSet.Fields, field => Assert.Equal(1, field.FormatCode));
        var row = Assert.Single(resultSet.Rows);
        Assert.Equal(1, row.Values[0]!.Value.Length);
        Assert.Equal(sizeof(int), row.Values[1]!.Value.Length);
    }

    [Fact]
    public async Task AdoNet_decodes_core_binary_scalar_families_and_edge_values()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand(
            "SELECT " +
            "$1::bool AS flag, " +
            "'-32768'::int2 AS small_value, " +
            "'9223372036854775807'::int8 AS big_value, " +
            "'4294967295'::oid AS oid_value, " +
            "1.25::float4 AS single_value, " +
            "'Infinity'::float8 AS double_value, " +
            "'123456789012345678901234567890.123456789'::numeric AS numeric_value, " +
            "'00112233-4455-6677-8899-aabbccddeeff'::uuid AS uuid_value, " +
            "decode('0001ff', 'hex') AS bytes_value, " +
            "'infinity'::date AS date_value, " +
            "'24:00:00'::time AS time_value, " +
            "'-infinity'::timestamp AS timestamp_value, " +
            "'2000-01-01 00:00:00+02'::timestamptz AS timestamptz_value, " +
            "'{\"answer\":42}'::json AS json_value, " +
            "'{\"enabled\":true}'::jsonb AS jsonb_value");
        command.Parameters.Add(new BlueTuskParameter<bool>(true));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(short.MinValue, reader.GetInt16(1));
        Assert.Equal(long.MaxValue, reader.GetInt64(2));
        Assert.Equal(uint.MaxValue, reader.GetFieldValue<uint>(3));
        Assert.Equal(1.25F, reader.GetFloat(4));
        Assert.Equal(double.PositiveInfinity, reader.GetDouble(5));
        Assert.Equal(
            BlueTuskNumeric.Parse("123456789012345678901234567890.123456789"),
            reader.GetFieldValue<BlueTuskNumeric>(6));
        Assert.Equal(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), reader.GetGuid(7));
        Assert.Equal(new byte[] { 0, 1, 255 }, reader.GetFieldValue<byte[]>(8));
        Assert.Equal(DateOnly.MaxValue, reader.GetFieldValue<DateOnly>(9));
        Assert.Equal(TimeSpan.FromDays(1), reader.GetFieldValue<TimeSpan>(10));
        Assert.Equal(DateTime.MinValue, reader.GetDateTime(11));
        Assert.Equal(
            new DateTimeOffset(1999, 12, 31, 22, 0, 0, TimeSpan.Zero),
            reader.GetFieldValue<DateTimeOffset>(12));
        Assert.Contains("42", reader.GetString(13), StringComparison.Ordinal);
        Assert.Contains("true", reader.GetString(14), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Binary_temporal_uuid_and_numeric_parameters_round_trip()
    {
        var uuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var date = new DateOnly(2026, 7, 21);
        var time = new TimeSpan(23, 59, 59) + TimeSpan.FromTicks(123_456 * TimeSpan.TicksPerMicrosecond);
        var timestamp = new DateTime(2026, 7, 21, 12, 34, 56).AddTicks(654_321 * TimeSpan.TicksPerMicrosecond);
        var timestampWithTimeZone = new DateTimeOffset(2026, 7, 21, 12, 34, 56, TimeSpan.FromHours(2));
        var numeric = BlueTuskNumeric.Parse("999999999999999999999999999999.000000001");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand(
            "SELECT $1::uuid, $2::date, $3::time, $4::timestamp, $5::timestamptz, $6::numeric");
        command.Parameters.Add(new BlueTuskParameter<Guid>(uuid));
        command.Parameters.Add(new BlueTuskParameter<DateOnly>(date));
        command.Parameters.Add(new BlueTuskParameter<TimeSpan>(time));
        command.Parameters.Add(new BlueTuskParameter<DateTime>(timestamp));
        command.Parameters.Add(new BlueTuskParameter<DateTimeOffset>(timestampWithTimeZone));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskNumeric>(numeric));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(uuid, reader.GetGuid(0));
        Assert.Equal(date, reader.GetFieldValue<DateOnly>(1));
        Assert.Equal(time, reader.GetFieldValue<TimeSpan>(2));
        Assert.Equal(timestamp, reader.GetDateTime(3));
        Assert.Equal(timestampWithTimeZone.UtcDateTime, reader.GetFieldValue<DateTimeOffset>(4).UtcDateTime);
        Assert.Equal(numeric, reader.GetFieldValue<BlueTuskNumeric>(5));
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

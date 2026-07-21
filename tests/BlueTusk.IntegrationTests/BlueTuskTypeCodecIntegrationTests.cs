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

    [Fact]
    public async Task Advanced_postgresql_scalars_decode_from_binary_results()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand(
            "SELECT $1::int4, " +
            "'(42,7)'::tid, " +
            "'24:00:00+05:30:45'::timetz, " +
            "'1 year 2 mons 3 days 04:05:06.789'::interval, " +
            "'infinity'::interval, " +
            "B'10110'::bit(5), " +
            "B'001011'::varbit, " +
            "'16/B374D848'::pg_lsn");
        command.Parameters.Add(new BlueTuskParameter<int>(1));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(new BlueTuskTupleId(42, 7), reader.GetFieldValue<BlueTuskTupleId>(1));
        Assert.Equal(
            new BlueTuskTimeWithTimeZone(TimeSpan.FromDays(1), new TimeSpan(5, 30, 45)),
            reader.GetFieldValue<BlueTuskTimeWithTimeZone>(2));
        Assert.Equal(new BlueTuskInterval(14, 3, 14_706_789_000), reader.GetFieldValue<BlueTuskInterval>(3));
        Assert.Equal(BlueTuskInterval.PositiveInfinity, reader.GetFieldValue<BlueTuskInterval>(4));
        Assert.Equal(new BlueTuskBitString("10110"), reader.GetFieldValue<BlueTuskBitString>(5));
        Assert.Equal(new BlueTuskBitString("001011"), reader.GetFieldValue<BlueTuskBitString>(6));
        Assert.Equal(
            BlueTuskLogSequenceNumber.Parse("16/B374D848"),
            reader.GetFieldValue<BlueTuskLogSequenceNumber>(7));
    }

    [Fact]
    public async Task Advanced_postgresql_scalar_parameters_round_trip_in_binary()
    {
        var tupleId = new BlueTuskTupleId(42, 7);
        var timeWithTimeZone = new BlueTuskTimeWithTimeZone(
            new TimeSpan(23, 59, 59) + TimeSpan.FromTicks(123_456 * TimeSpan.TicksPerMicrosecond),
            new TimeSpan(-8, -30, -45));
        var interval = new BlueTuskInterval(-10, -3, 14_106_789_000);
        var bits = new BlueTuskBitString("1011001");
        var lsn = BlueTuskLogSequenceNumber.Parse("16/B374D848");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand(
            "SELECT $1::tid, $2::timetz, $3::interval, $4::varbit, $5::pg_lsn");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskTupleId>(tupleId));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskTimeWithTimeZone>(timeWithTimeZone));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskInterval>(interval));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskBitString>(bits));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskLogSequenceNumber>(lsn));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(tupleId, reader.GetFieldValue<BlueTuskTupleId>(0));
        Assert.Equal(timeWithTimeZone, reader.GetFieldValue<BlueTuskTimeWithTimeZone>(1));
        Assert.Equal(interval, reader.GetFieldValue<BlueTuskInterval>(2));
        Assert.Equal(bits, reader.GetFieldValue<BlueTuskBitString>(3));
        Assert.Equal(lsn, reader.GetFieldValue<BlueTuskLogSequenceNumber>(4));
    }

    [Fact]
    public async Task Network_scalar_parameters_round_trip_in_binary()
    {
        var inet = BlueTuskNetworkAddress.Parse("192.168.1.5/24");
        var cidr = BlueTuskNetworkAddress.Parse("2001:db8::/32", isCidr: true);
        var macaddr = BlueTuskMacAddress.Parse("08:00:2b:01:02:03");
        var macaddr8 = BlueTuskMacAddress8.Parse("08:00:2b:01:02:03:04:05");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand(
            "SELECT $1::inet, $2::cidr, $3::macaddr, $4::macaddr8");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskNetworkAddress>(inet));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskNetworkAddress>(cidr));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskMacAddress>(macaddr));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskMacAddress8>(macaddr8));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(inet, reader.GetFieldValue<BlueTuskNetworkAddress>(0));
        Assert.Equal(cidr, reader.GetFieldValue<BlueTuskNetworkAddress>(1));
        Assert.Equal(macaddr, reader.GetFieldValue<BlueTuskMacAddress>(2));
        Assert.Equal(macaddr8, reader.GetFieldValue<BlueTuskMacAddress8>(3));
    }

    [Fact]
    public async Task Geometric_scalar_parameters_round_trip_in_binary()
    {
        var first = new BlueTuskPoint(1.5, -2.25);
        var second = new BlueTuskPoint(3, 4);
        var third = new BlueTuskPoint(-5.5, 6.75);
        var point = first;
        var line = new BlueTuskLine(1, 2, 3);
        var lineSegment = new BlueTuskLineSegment(first, second);
        var box = new BlueTuskBox(first, second);
        var path = new BlueTuskPath(new[] { first, second, third }, isClosed: false);
        var polygon = new BlueTuskPolygon(new[] { first, second, third });
        var circle = new BlueTuskCircle(first, 3.5);
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand(
            "SELECT $1::point, $2::line, $3::lseg, $4::box, $5::path, $6::polygon, $7::circle");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskPoint>(point));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskLine>(line));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskLineSegment>(lineSegment));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskBox>(box));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskPath>(path));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskPolygon>(polygon));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskCircle>(circle));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(point, reader.GetFieldValue<BlueTuskPoint>(0));
        Assert.Equal(line, reader.GetFieldValue<BlueTuskLine>(1));
        Assert.Equal(lineSegment, reader.GetFieldValue<BlueTuskLineSegment>(2));
        Assert.Equal(box, reader.GetFieldValue<BlueTuskBox>(3));
        Assert.Equal(path, reader.GetFieldValue<BlueTuskPath>(4));
        Assert.Equal(polygon, reader.GetFieldValue<BlueTuskPolygon>(5));
        Assert.Equal(circle, reader.GetFieldValue<BlueTuskCircle>(6));
    }

    [Fact]
    public async Task Text_search_parameters_round_trip_in_binary()
    {
        var vector = BlueTuskTextSearchVector.Parse("'cat':3 'fat':2A,4B 'rat':5");
        var query = BlueTuskTextSearchQuery.Parse("fat:AB & (rat | !cat:*)");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand("SELECT $1::tsvector, $2::tsquery");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskTextSearchVector>(vector));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskTextSearchQuery>(query));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(vector, reader.GetFieldValue<BlueTuskTextSearchVector>(0));
        Assert.Equal(query, reader.GetFieldValue<BlueTuskTextSearchQuery>(1));
    }

    [Fact]
    public async Task Data_source_discovers_caches_and_reloads_catalogue_type_metadata()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using (var connection = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            var registry = dataSource.TypeRegistry;
            Assert.True(registry.Types.Count > 100);
            Assert.True(registry.TryGetType(new BlueTuskTypeId(1007), out var array));
            Assert.Equal(BlueTuskTypeKind.Array, array!.Kind);
            Assert.Equal(new BlueTuskTypeId(23), array.ElementType);
            Assert.True(registry.TryGetType(new BlueTuskTypeId(3904), out var range));
            Assert.Equal(BlueTuskTypeKind.Range, range!.Kind);
            Assert.Equal(new BlueTuskTypeId(23), range.RangeSubtype);
            Assert.True(registry.TryGetType(new BlueTuskTypeId(4451), out var multirange));
            Assert.Equal(BlueTuskTypeKind.Multirange, multirange!.Kind);
            Assert.Equal(new BlueTuskTypeId(23), multirange.RangeSubtype);
            Assert.True(registry.TryGetType(new BlueTuskTypeId(71), out var composite));
            Assert.Equal(BlueTuskTypeKind.Composite, composite!.Kind);
            Assert.True(registry.TryGetCodec(new BlueTuskTypeId(23), out var codec));
            Assert.IsType<BlueTuskInt32Codec>(codec);
        }

        var cached = dataSource.TypeRegistry;
        await using (var secondConnection = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            Assert.Same(cached, dataSource.TypeRegistry);
        }

        await dataSource.ReloadTypesAsync(CancellationToken.None);
        Assert.NotSame(cached, dataSource.TypeRegistry);
        Assert.Equal(cached.Types.Count, dataSource.TypeRegistry.Types.Count);
    }

    [Fact]
    public async Task Money_uses_discovered_locale_scale_for_text_and_binary_round_trips()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using (var connection = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            Assert.True(dataSource.TypeRegistry.TryGetCodec(BlueTuskBuiltInTypes.Money.Id, out var registered));
            var codec = Assert.IsType<BlueTuskMoneyCodec>(registered);
            Assert.Equal(2, codec.FractionalDigits);
        }

        var value = new BlueTuskMoney(123_456, 2);
        await using (var binaryCommand = dataSource.CreateCommand("SELECT $1::money"))
        {
            binaryCommand.Parameters.Add(new BlueTuskParameter<BlueTuskMoney>(value));
            await using var reader = await binaryCommand.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(value, reader.GetFieldValue<BlueTuskMoney>(0));
        }

        await using (var textCommand = dataSource.CreateCommand("SELECT 1234.56::money"))
        await using (var reader = await textCommand.ExecuteReaderAsync(CancellationToken.None))
        {
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(value, reader.GetFieldValue<BlueTuskMoney>(0));
        }
    }

    [Fact]
    public async Task Data_source_builder_binds_runtime_codec_by_catalogue_name()
    {
        var builder = new BlueTuskDataSourceBuilder(GetConnectionString());
        builder.Types.Register("pg_catalog", "jsonpath", new TestJsonPathCodec());
        await using var dataSource = builder.Build();
        var value = new TestJsonPath("$.answer");
        var normalizedValue = new TestJsonPath("$.\"answer\"");

        await using (var binaryCommand = dataSource.CreateCommand("SELECT $1::jsonpath"))
        {
            binaryCommand.Parameters.Add(new BlueTuskParameter<TestJsonPath>(value));
            await using var reader = await binaryCommand.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(normalizedValue, reader.GetFieldValue<TestJsonPath>(0));
        }

        await using (var textCommand = dataSource.CreateCommand("SELECT '$.answer'::jsonpath"))
        await using (var reader = await textCommand.ExecuteReaderAsync(CancellationToken.None))
        {
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(normalizedValue, reader.GetFieldValue<TestJsonPath>(0));
            Assert.Equal("jsonpath", reader.GetDataTypeName(0));
        }

        Assert.True(dataSource.TypeRegistry.TryGetType(typeof(TestJsonPath), out var type, out var codec));
        Assert.Equal(4072U, type!.Id.Oid);
        Assert.IsType<TestJsonPathCodec>(codec);
    }

    private readonly record struct TestJsonPath(string Value);

    private sealed class TestJsonPathCodec : BlueTuskCodec<TestJsonPath>
    {
        public override TestJsonPath ReadTyped(
            ref BlueTuskReader reader,
            BlueTuskDataFormat format,
            BlueTuskTypeDescriptor type)
        {
            if (format == BlueTuskDataFormat.Binary && reader.ReadByte() != 1)
            {
                throw new InvalidOperationException("PostgreSQL jsonpath binary version is not supported.");
            }

            return new TestJsonPath(reader.ReadRemainingUtf8());
        }

        public override void WriteTyped(
            ref BlueTuskWriter writer,
            TestJsonPath value,
            BlueTuskDataFormat format,
            BlueTuskTypeDescriptor type)
        {
            if (format == BlueTuskDataFormat.Binary)
            {
                writer.WriteByte(1);
            }

            writer.WriteUtf8(value.Value);
        }
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

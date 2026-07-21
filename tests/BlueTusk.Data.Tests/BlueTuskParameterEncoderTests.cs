using System.Buffers.Binary;
using System.Data;
using System.Text;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskParameterEncoderTests
{
    private static readonly uint[] AdvancedTypeOids = [27, 1186, 1266, 1562, 3220];
    private static readonly int[] AdvancedPayloadLengths = [6, 16, 12, 5, 8];
    private static readonly uint[] GeometricTypeOids = [600, 601, 602, 603, 604, 628, 718];
    private static readonly int[] GeometricPayloadLengths = [16, 32, 37, 32, 36, 24, 24];

    [Fact]
    public void Encodes_int32_as_binary_int4()
    {
        var encoded = BlueTuskParameterEncoder.Encode(new BlueTuskParameter<int>(42));

        Assert.Equal(23U, encoded.TypeOid);
        Assert.Equal(1, encoded.FormatCode);
        Assert.Equal(42, BinaryPrimitives.ReadInt32BigEndian(encoded.Value!.Value.Span));
    }

    [Fact]
    public void Encodes_null_with_an_explicit_db_type()
    {
        var encoded = BlueTuskParameterEncoder.Encode(
            new BlueTuskParameter(DBNull.Value) { DbType = DbType.String });

        Assert.Equal(25U, encoded.TypeOid);
        Assert.Equal(0, encoded.FormatCode);
        Assert.Null(encoded.Value);
    }

    [Fact]
    public void Encodes_custom_type_text_with_an_explicit_oid()
    {
        var encoded = BlueTuskParameterEncoder.Encode(
            new BlueTuskParameter("custom-value") { PostgreSqlTypeOid = 99_999 });

        Assert.Equal(99_999U, encoded.TypeOid);
        Assert.Equal(0, encoded.FormatCode);
        Assert.Equal("custom-value", Encoding.UTF8.GetString(encoded.Value!.Value.Span));
    }

    [Fact]
    public void Rejects_an_untyped_null()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => BlueTuskParameterEncoder.Encode(new BlueTuskParameter(null)));

        Assert.Contains("requires DbType or PostgreSqlTypeOid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Encodes_uuid_and_temporal_values_in_binary_format()
    {
        var guid = BlueTuskParameterEncoder.Encode(
            new BlueTuskParameter<Guid>(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")));
        var date = BlueTuskParameterEncoder.Encode(new BlueTuskParameter<DateOnly>(new DateOnly(2000, 1, 1)));
        var time = BlueTuskParameterEncoder.Encode(new BlueTuskParameter<TimeSpan>(TimeSpan.FromHours(24)));
        var timestamp = BlueTuskParameterEncoder.Encode(
            new BlueTuskParameter<DateTime>(new DateTime(2000, 1, 1).AddTicks(TimeSpan.TicksPerMicrosecond)));

        Assert.Equal("00112233445566778899AABBCCDDEEFF", Convert.ToHexString(guid.Value!.Value.Span));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(date.Value!.Value.Span));
        Assert.Equal(86_400_000_000, BinaryPrimitives.ReadInt64BigEndian(time.Value!.Value.Span));
        Assert.Equal(1, BinaryPrimitives.ReadInt64BigEndian(timestamp.Value!.Value.Span));
        Assert.All(new[] { guid, date, time, timestamp }, value => Assert.Equal(1, value.FormatCode));
    }

    [Fact]
    public void Encodes_arbitrary_precision_numeric_as_lossless_binary()
    {
        var value = BlueTuskNumeric.Parse("123456789012345678901234567890.123456789");

        var encoded = BlueTuskParameterEncoder.Encode(new BlueTuskParameter<BlueTuskNumeric>(value));

        Assert.Equal(1700U, encoded.TypeOid);
        Assert.Equal(1, encoded.FormatCode);
        var reader = new BlueTuskReader(encoded.Value!.Value.Span);
        Assert.Equal(
            value,
            new BlueTuskNumericCodec().ReadTyped(
                ref reader,
                BlueTuskDataFormat.Binary,
                BlueTuskBuiltInTypes.Numeric));
    }

    [Fact]
    public void Encodes_postgresql_specific_scalar_values_in_binary()
    {
        var values = new BlueTuskParameter[]
        {
            new BlueTuskParameter<BlueTuskTupleId>(new BlueTuskTupleId(42, 7)),
            new BlueTuskParameter<BlueTuskInterval>(new BlueTuskInterval(14, 3, 4_000_005)),
            new BlueTuskParameter<BlueTuskTimeWithTimeZone>(
                new BlueTuskTimeWithTimeZone(TimeSpan.FromHours(12), TimeSpan.FromHours(-8))),
            new BlueTuskParameter<BlueTuskBitString>(new BlueTuskBitString("10110")),
            new BlueTuskParameter<BlueTuskLogSequenceNumber>(
                BlueTuskLogSequenceNumber.Parse("16/B374D848")),
        };

        var encoded = values.Select(value => BlueTuskParameterEncoder.Encode(value)).ToArray();

        Assert.Equal(AdvancedTypeOids, encoded.Select(item => item.TypeOid));
        Assert.Equal(AdvancedPayloadLengths, encoded.Select(item => item.Value!.Value.Length));
        Assert.All(encoded, item => Assert.Equal(1, item.FormatCode));
    }

    [Fact]
    public void Encodes_network_values_in_binary()
    {
        var inet = BlueTuskParameterEncoder.Encode(
            new BlueTuskParameter<BlueTuskNetworkAddress>(
                BlueTuskNetworkAddress.Parse("192.168.1.5/24")));
        var cidr = BlueTuskParameterEncoder.Encode(
            new BlueTuskParameter<BlueTuskNetworkAddress>(
                BlueTuskNetworkAddress.Parse("2001:db8::/32", isCidr: true)));
        var macaddr = BlueTuskParameterEncoder.Encode(
            new BlueTuskParameter<BlueTuskMacAddress>(BlueTuskMacAddress.Parse("08:00:2b:01:02:03")));
        var macaddr8 = BlueTuskParameterEncoder.Encode(
            new BlueTuskParameter<BlueTuskMacAddress8>(BlueTuskMacAddress8.Parse("08:00:2b:01:02:03:04:05")));

        Assert.Equal(869U, inet.TypeOid);
        Assert.Equal(650U, cidr.TypeOid);
        Assert.Equal(829U, macaddr.TypeOid);
        Assert.Equal(774U, macaddr8.TypeOid);
        Assert.Equal(1, inet.FormatCode);
        Assert.Equal(1, cidr.FormatCode);
        Assert.Equal(1, macaddr.FormatCode);
        Assert.Equal(1, macaddr8.FormatCode);
    }

    [Fact]
    public void Encodes_geometric_values_in_binary()
    {
        var points = new[] { new BlueTuskPoint(1, 2), new BlueTuskPoint(3, 4) };
        var values = new BlueTuskParameter[]
        {
            new BlueTuskParameter<BlueTuskPoint>(points[0]),
            new BlueTuskParameter<BlueTuskLineSegment>(new BlueTuskLineSegment(points[0], points[1])),
            new BlueTuskParameter<BlueTuskPath>(new BlueTuskPath(points, isClosed: false)),
            new BlueTuskParameter<BlueTuskBox>(new BlueTuskBox(points[0], points[1])),
            new BlueTuskParameter<BlueTuskPolygon>(new BlueTuskPolygon(points)),
            new BlueTuskParameter<BlueTuskLine>(new BlueTuskLine(1, 2, 3)),
            new BlueTuskParameter<BlueTuskCircle>(new BlueTuskCircle(points[0], 5)),
        };

        var encoded = values.Select(value => BlueTuskParameterEncoder.Encode(value)).ToArray();

        Assert.Equal(GeometricTypeOids, encoded.Select(item => item.TypeOid));
        Assert.Equal(GeometricPayloadLengths, encoded.Select(item => item.Value!.Value.Length));
        Assert.All(encoded, item => Assert.Equal(1, item.FormatCode));
    }

    [Fact]
    public void Encodes_text_search_values_in_binary()
    {
        var vector = BlueTuskTextSearchVector.Parse("'fat':2A 'rat':3");
        var query = BlueTuskTextSearchQuery.Parse("fat:AB & rat:*");

        var encodedVector = BlueTuskParameterEncoder.Encode(
            new BlueTuskParameter<BlueTuskTextSearchVector>(vector));
        var encodedQuery = BlueTuskParameterEncoder.Encode(
            new BlueTuskParameter<BlueTuskTextSearchQuery>(query));

        Assert.Equal(3614U, encodedVector.TypeOid);
        Assert.Equal(3615U, encodedQuery.TypeOid);
        Assert.Equal(1, encodedVector.FormatCode);
        Assert.Equal(1, encodedQuery.FormatCode);
        Assert.Equal(BlueTuskTextSearchVectorCodec.GetBinarySize(vector), encodedVector.Value!.Value.Length);
        Assert.Equal(BlueTuskTextSearchQueryCodec.GetBinarySize(query), encodedQuery.Value!.Value.Length);
    }

    [Fact]
    public void Encodes_money_raw_minor_units_and_validates_discovered_scale()
    {
        var value = new BlueTuskMoney(123_456, 2);
        var types = BlueTuskTypeCatalogue.BuildRegistry(
            [],
            moneyFormat: new BlueTuskMoneyFormat("en_US.UTF-8", 2));

        var encoded = BlueTuskParameterEncoder.Encode(new BlueTuskParameter<BlueTuskMoney>(value), types);

        Assert.Equal(790U, encoded.TypeOid);
        Assert.Equal(1, encoded.FormatCode);
        Assert.Equal(123_456, BinaryPrimitives.ReadInt64BigEndian(encoded.Value!.Value.Span));
        Assert.Throws<InvalidOperationException>(() =>
            BlueTuskParameterEncoder.Encode(
                new BlueTuskParameter<BlueTuskMoney>(new BlueTuskMoney(123_456, 3)),
                types));
    }

    [Fact]
    public void Encodes_registered_runtime_type_and_grows_its_buffer()
    {
        var descriptor = new BlueTuskTypeDescriptor
        {
            Id = new BlueTuskTypeId(91_100),
            Schema = "app",
            Name = "large_value",
            Kind = BlueTuskTypeKind.Base,
        };
        var types = new BlueTuskTypeRegistryBuilder()
            .Register(descriptor, new LargeValueCodec())
            .Build();
        var value = new LargeValue(new string('x', 1024));

        var encoded = BlueTuskParameterEncoder.Encode(new BlueTuskParameter<LargeValue>(value), types);

        Assert.Equal(91_100U, encoded.TypeOid);
        Assert.Equal(1, encoded.FormatCode);
        Assert.Equal(1024, encoded.Value!.Value.Length);
        Assert.All(encoded.Value.Value.ToArray(), item => Assert.Equal((byte)'x', item));
    }

    private readonly record struct LargeValue(string Value);

    private sealed class LargeValueCodec : BlueTuskCodec<LargeValue>
    {
        public override LargeValue ReadTyped(
            ref BlueTuskReader reader,
            BlueTuskDataFormat format,
            BlueTuskTypeDescriptor type) => new(reader.ReadRemainingUtf8());

        public override void WriteTyped(
            ref BlueTuskWriter writer,
            LargeValue value,
            BlueTuskDataFormat format,
            BlueTuskTypeDescriptor type) => writer.WriteUtf8(value.Value);
    }
}

using System.Buffers.Binary;
using System.Data;
using System.Text;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskParameterEncoderTests
{
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
    public void Encodes_arbitrary_precision_numeric_as_lossless_text()
    {
        var value = BlueTuskNumeric.Parse("123456789012345678901234567890.123456789");

        var encoded = BlueTuskParameterEncoder.Encode(new BlueTuskParameter<BlueTuskNumeric>(value));

        Assert.Equal(1700U, encoded.TypeOid);
        Assert.Equal(0, encoded.FormatCode);
        Assert.Equal(value.ToString(), Encoding.UTF8.GetString(encoded.Value!.Value.Span));
    }
}

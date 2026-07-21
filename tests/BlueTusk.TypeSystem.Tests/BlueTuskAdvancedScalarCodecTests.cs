using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskAdvancedScalarCodecTests
{
    [Fact]
    public void Time_with_time_zone_preserves_time_offset_and_binary_sign_convention()
    {
        var value = new BlueTuskTimeWithTimeZone(
            new TimeSpan(0, 24, 0, 0, 0),
            new TimeSpan(0, 5, 30, 45, 0));
        var bytes = Write(
            new BlueTuskTimeWithTimeZoneCodec(),
            BlueTuskBuiltInTypes.TimeWithTimeZone,
            value,
            BlueTuskDataFormat.Binary);

        Assert.Equal(86_400_000_000, BinaryPrimitives.ReadInt64BigEndian(bytes));
        Assert.Equal(-19_845, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8)));
        AssertRoundTrip(
            new BlueTuskTimeWithTimeZoneCodec(),
            BlueTuskBuiltInTypes.TimeWithTimeZone,
            value);
        Assert.Equal(value, ReadText(
            new BlueTuskTimeWithTimeZoneCodec(),
            BlueTuskBuiltInTypes.TimeWithTimeZone,
            "24:00:00+05:30:45"));
    }

    [Theory]
    [InlineData("1 year 2 mons 3 days 04:05:06.789")]
    [InlineData("@ 1 year 2 mons 3 days 4 hours 5 mins 6.789 secs")]
    [InlineData("+1-2 +3 +4:05:06.789")]
    [InlineData("P1Y2M3DT4H5M6.789S")]
    public void Interval_parses_every_postgresql_output_style(string text)
    {
        var expected = new BlueTuskInterval(14, 3, 14_706_789_000);

        Assert.Equal(expected, BlueTuskInterval.Parse(text));
    }

    [Theory]
    [InlineData("-10 mons -3 days +03:55:06.789")]
    [InlineData("@ 10 mons 3 days -3 hours -55 mins -6.789 secs ago")]
    [InlineData("-0-10 -3 +3:55:06.789")]
    [InlineData("P-10M-3DT3H55M6.789S")]
    public void Interval_preserves_independent_component_signs(string text)
    {
        var expected = new BlueTuskInterval(-10, -3, 14_106_789_000);

        Assert.Equal(expected, BlueTuskInterval.Parse(text));
    }

    [Fact]
    public void Interval_binary_preserves_fields_and_infinities()
    {
        var codec = new BlueTuskIntervalCodec();
        var value = new BlueTuskInterval(-14, 3, -14_706_789_000);

        AssertRoundTrip(codec, BlueTuskBuiltInTypes.Interval, value);
        AssertRoundTrip(codec, BlueTuskBuiltInTypes.Interval, BlueTuskInterval.PositiveInfinity);
        AssertRoundTrip(codec, BlueTuskBuiltInTypes.Interval, BlueTuskInterval.NegativeInfinity);

        var bytes = Write(codec, BlueTuskBuiltInTypes.Interval, value, BlueTuskDataFormat.Binary);
        Assert.Equal(value.Microseconds, BinaryPrimitives.ReadInt64BigEndian(bytes));
        Assert.Equal(value.Days, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8)));
        Assert.Equal(value.Months, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(12)));
    }

    [Fact]
    public void Bit_string_binary_carries_exact_bit_length_and_zero_padding()
    {
        var codec = new BlueTuskBitStringCodec();
        var value = new BlueTuskBitString("10110");
        var bytes = Write(codec, BlueTuskBuiltInTypes.Varbit, value, BlueTuskDataFormat.Binary);

        Assert.Equal(5, BinaryPrimitives.ReadInt32BigEndian(bytes));
        Assert.Equal(0xB0, bytes[4]);
        AssertRoundTrip(codec, BlueTuskBuiltInTypes.Varbit, value);

        Assert.Throws<InvalidOperationException>(ReadInvalidBitPadding);
    }

    [Fact]
    public void Log_sequence_number_round_trips_in_text_and_binary()
    {
        var value = BlueTuskLogSequenceNumber.Parse("16/B374D848");

        Assert.Equal("16/B374D848", value.ToString());
        AssertRoundTrip(
            new BlueTuskLogSequenceNumberCodec(),
            BlueTuskBuiltInTypes.PgLsn,
            value);
    }

    [Fact]
    public void Tuple_id_binary_is_block_then_offset()
    {
        var codec = new BlueTuskTupleIdCodec();
        var value = new BlueTuskTupleId(0x01020304, 0x0506);
        var bytes = Write(codec, BlueTuskBuiltInTypes.Tid, value, BlueTuskDataFormat.Binary);

        Assert.Equal(Convert.FromHexString("010203040506"), bytes);
        Assert.Equal(value, BlueTuskTupleId.Parse("(16909060,1286)"));
        AssertRoundTrip(codec, BlueTuskBuiltInTypes.Tid, value);
    }

    private static void AssertRoundTrip<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value)
    {
        foreach (var format in new[] { BlueTuskDataFormat.Text, BlueTuskDataFormat.Binary })
        {
            var bytes = Write(codec, type, value, format);
            var reader = new BlueTuskReader(bytes);
            Assert.Equal(value, codec.ReadTyped(ref reader, format, type));
            Assert.Equal(0, reader.Remaining);
        }
    }

    private static byte[] Write<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        BlueTuskDataFormat format)
    {
        Span<byte> destination = stackalloc byte[512];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, format, type);
        return destination[..writer.WrittenCount].ToArray();
    }

    private static T ReadText<T>(BlueTuskCodec<T> codec, BlueTuskTypeDescriptor type, string value)
    {
        var reader = new BlueTuskReader(Encoding.UTF8.GetBytes(value));
        return codec.ReadTyped(ref reader, BlueTuskDataFormat.Text, type);
    }

    private static void ReadInvalidBitPadding()
    {
        var reader = new BlueTuskReader(Convert.FromHexString("00000005B1"));
        _ = new BlueTuskBitStringCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.Varbit);
    }
}

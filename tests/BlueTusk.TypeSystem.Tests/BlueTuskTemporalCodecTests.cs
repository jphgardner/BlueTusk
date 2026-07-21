using System.Buffers.Binary;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskTemporalCodecTests
{
    [Fact]
    public void PostgreSql_epoch_is_zero_for_date_and_timestamp()
    {
        var dateBytes = WriteBinary(
            new BlueTuskDateCodec(),
            BlueTuskBuiltInTypes.Date,
            new DateOnly(2000, 1, 1));
        var timestampBytes = WriteBinary(
            new BlueTuskTimestampCodec(),
            BlueTuskBuiltInTypes.Timestamp,
            new DateTime(2000, 1, 1));

        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(dateBytes));
        Assert.Equal(0, BinaryPrimitives.ReadInt64BigEndian(timestampBytes));
    }

    [Fact]
    public void Timestamp_binary_has_microsecond_precision()
    {
        var value = new DateTime(2000, 1, 1).AddTicks(TimeSpan.TicksPerMicrosecond);
        var bytes = WriteBinary(
            new BlueTuskTimestampCodec(),
            BlueTuskBuiltInTypes.Timestamp,
            value);

        Assert.Equal(1, BinaryPrimitives.ReadInt64BigEndian(bytes));
        Assert.Equal(
            value,
            ReadBinary(new BlueTuskTimestampCodec(), BlueTuskBuiltInTypes.Timestamp, bytes));
    }

    [Fact]
    public void Timestamp_with_time_zone_normalises_to_utc_on_the_wire()
    {
        var value = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.FromHours(2));
        var bytes = WriteBinary(
            new BlueTuskTimestampWithTimeZoneCodec(),
            BlueTuskBuiltInTypes.TimestampWithTimeZone,
            value);

        Assert.Equal(-7_200_000_000, BinaryPrimitives.ReadInt64BigEndian(bytes));
        Assert.Equal(
            value.UtcDateTime,
            ReadBinary(
                new BlueTuskTimestampWithTimeZoneCodec(),
                BlueTuskBuiltInTypes.TimestampWithTimeZone,
                bytes).UtcDateTime);
    }

    [Fact]
    public void Time_supports_postgresql_24_hour_boundary()
    {
        var value = TimeSpan.FromDays(1);
        var codec = new BlueTuskTimeCodec();
        var bytes = WriteBinary(codec, BlueTuskBuiltInTypes.Time, value);

        Assert.Equal(86_400_000_000, BinaryPrimitives.ReadInt64BigEndian(bytes));
        Assert.Equal(value, ReadBinary(codec, BlueTuskBuiltInTypes.Time, bytes));
        Assert.Equal(value, ReadText(codec, BlueTuskBuiltInTypes.Time, "24:00:00"));
    }

    [Fact]
    public void Date_and_timestamps_map_postgresql_infinities()
    {
        AssertInfinity(new BlueTuskDateCodec(), BlueTuskBuiltInTypes.Date, DateOnly.MinValue, DateOnly.MaxValue);
        AssertInfinity(
            new BlueTuskTimestampCodec(),
            BlueTuskBuiltInTypes.Timestamp,
            DateTime.MinValue,
            DateTime.MaxValue);
        AssertInfinity(
            new BlueTuskTimestampWithTimeZoneCodec(),
            BlueTuskBuiltInTypes.TimestampWithTimeZone,
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue);
    }

    private static void AssertInfinity<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T negativeInfinity,
        T positiveInfinity)
    {
        Assert.Equal(negativeInfinity, ReadText(codec, type, "-infinity"));
        Assert.Equal(positiveInfinity, ReadText(codec, type, "infinity"));
        Assert.Equal(negativeInfinity, ReadBinary(codec, type, WriteBinary(codec, type, negativeInfinity)));
        Assert.Equal(positiveInfinity, ReadBinary(codec, type, WriteBinary(codec, type, positiveInfinity)));
    }

    private static byte[] WriteBinary<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value)
    {
        Span<byte> destination = stackalloc byte[32];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, BlueTuskDataFormat.Binary, type);
        return destination[..writer.WrittenCount].ToArray();
    }

    private static T ReadBinary<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        byte[] value)
    {
        var reader = new BlueTuskReader(value);
        return codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, type);
    }

    private static T ReadText<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        string value)
    {
        var reader = new BlueTuskReader(System.Text.Encoding.UTF8.GetBytes(value));
        return codec.ReadTyped(ref reader, BlueTuskDataFormat.Text, type);
    }
}

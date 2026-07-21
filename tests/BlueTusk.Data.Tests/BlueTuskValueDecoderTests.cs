using System.Text;
using BlueTusk.Client;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskValueDecoderTests
{
    [Fact]
    public void Registry_decodes_binary_scalar_and_temporal_values()
    {
        Assert.Equal(42, DecodeParameter(new BlueTuskParameter<int>(42)));
        Assert.Equal(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            DecodeParameter(new BlueTuskParameter<Guid>(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"))));
        Assert.Equal(
            new DateOnly(2000, 1, 2),
            DecodeParameter(new BlueTuskParameter<DateOnly>(new DateOnly(2000, 1, 2))));
        Assert.Equal(
            TimeSpan.FromDays(1),
            DecodeParameter(new BlueTuskParameter<TimeSpan>(TimeSpan.FromDays(1))));
        Assert.Equal(
            new DateTime(2000, 1, 1).AddTicks(TimeSpan.TicksPerMicrosecond),
            DecodeParameter(
                new BlueTuskParameter<DateTime>(
                    new DateTime(2000, 1, 1).AddTicks(TimeSpan.TicksPerMicrosecond))));
    }

    [Fact]
    public void Unknown_binary_values_preserve_oid_format_and_bytes()
    {
        byte[] bytes = [0x01, 0x02, 0x03];
        var field = Field(99_999, formatCode: 1);

        var value = Assert.IsType<BlueTuskUnknownValue>(BlueTuskValueDecoder.Decode(field, bytes));

        Assert.Equal(99_999U, value.Type.Id.Oid);
        Assert.Equal(BlueTuskDataFormat.Binary, value.Format);
        Assert.Equal(bytes, value.Data.ToArray());
    }

    [Fact]
    public void Data_reader_exposes_arbitrary_numeric_and_checked_decimal_conversion()
    {
        var numeric = BlueTuskNumeric.Parse("12345.6789");
        var bytes = WriteBinary(new BlueTuskNumericCodec(), BlueTuskBuiltInTypes.Numeric, numeric);
        using var reader = CreateReader(Field(1700, formatCode: 1), bytes);

        Assert.True(reader.Read());
        Assert.Equal(typeof(BlueTuskNumeric), reader.GetFieldType(0));
        Assert.Equal(numeric, reader.GetFieldValue<BlueTuskNumeric>(0));
        Assert.Equal(12345.6789m, reader.GetDecimal(0));
    }

    [Fact]
    public void Data_reader_stream_accessors_cover_bytea_text_and_json()
    {
        byte[] bytes = [0, 1, 2, 255];
        using var byteaReader = CreateReader(Field(17, formatCode: 1), bytes);
        Assert.True(byteaReader.Read());
        using var stream = byteaReader.GetStream(0);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.Equal(bytes, memory.ToArray());

        const string json = "{\"answer\":42}";
        using var jsonReader = CreateReader(Field(114, formatCode: 0), Encoding.UTF8.GetBytes(json));
        Assert.True(jsonReader.Read());
        using var textReader = jsonReader.GetTextReader(0);
        Assert.Equal(json, textReader.ReadToEnd());
    }

    private static object DecodeParameter(BlueTuskParameter parameter)
    {
        var encoded = BlueTuskParameterEncoder.Encode(parameter);
        return BlueTuskValueDecoder.Decode(
            Field(encoded.TypeOid, encoded.FormatCode),
            encoded.Value)!;
    }

    private static BlueTuskDataReader CreateReader(BlueTuskFieldDescription field, ReadOnlyMemory<byte> value) =>
        new(
            new BlueTuskQueryResult(
            [
                new BlueTuskResultSet(
                    [field],
                    [new BlueTuskDataRow([value])],
                    "SELECT 1"),
            ]),
            connectionToClose: null);

    private static BlueTuskFieldDescription Field(uint oid, short formatCode) =>
        new("value", 0, 0, oid, -1, -1, formatCode);

    private static byte[] WriteBinary<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value)
    {
        Span<byte> destination = stackalloc byte[128];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, BlueTuskDataFormat.Binary, type);
        return destination[..writer.WrittenCount].ToArray();
    }
}

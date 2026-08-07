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
    public void Data_reader_converts_lossless_numeric_arrays_to_decimal_arrays()
    {
        var arrayType = new BlueTuskTypeDescriptor
        {
            Id = new BlueTuskTypeId(1231),
            Schema = "pg_catalog",
            Name = "_numeric",
            Kind = BlueTuskTypeKind.Array,
            ElementType = BlueTuskBuiltInTypes.Numeric.Id,
        };
        var codec = new BlueTuskArrayCodec(
            BlueTuskBuiltInTypes.Numeric,
            new BlueTuskNumericCodec());
        var types = new BlueTuskTypeRegistryBuilder()
            .Register(BlueTuskBuiltInTypes.Numeric, new BlueTuskNumericCodec())
            .Register(arrayType, codec)
            .Build();
        BlueTuskNumeric[] canonical =
        [
            (BlueTuskNumeric)12.3400m,
            (BlueTuskNumeric)(-0.1250m),
        ];
        var bytes = Write(codec, arrayType, canonical);
        using var reader = CreateReader(Field(1231, formatCode: 1), bytes, types);

        Assert.True(reader.Read());
        Assert.Equal([12.3400m, -0.1250m], reader.GetFieldValue<decimal[]>(0));
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

    [Fact]
    public void Data_reader_typed_getters_use_generic_codec_without_object_dispatch()
    {
        var type = new BlueTuskTypeDescriptor
        {
            Id = new BlueTuskTypeId(80_001),
            Schema = "public",
            Name = "tracked_int",
            Kind = BlueTuskTypeKind.Base,
        };
        var codec = new TrackingInt32Codec();
        var types = new BlueTuskTypeRegistryBuilder()
            .Register(type, codec)
            .Build();
        byte[] bytes = [0, 0, 0, 42];
        using var reader = CreateReader(Field(type.Id.Oid, formatCode: 1), bytes, types);

        Assert.True(reader.Read());
        Assert.Equal(42, reader.GetFieldValue<int>(0));
        Assert.Equal(42, reader.GetInt32(0));
        Assert.Equal(2, codec.TypedReadCount);
        Assert.Equal(0, codec.ObjectReadCount);

        Assert.Equal(42, reader.GetValue(0));
        Assert.Equal(3, codec.TypedReadCount);
        Assert.Equal(1, codec.ObjectReadCount);
    }

    [Fact]
    public void Buffered_streams_are_read_only_views_that_outlive_the_reader()
    {
        byte[] bytes = [0, 1, 2, 3];
        var reader = CreateReader(Field(17, formatCode: 1), bytes);
        Assert.True(reader.Read());
        var stream = reader.GetStream(0);
        reader.Dispose();

        bytes[0] = 42;
        Assert.False(stream.CanWrite);
        Assert.Equal(42, stream.ReadByte());
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(1));
        stream.Dispose();
    }

    private static object DecodeParameter(BlueTuskParameter parameter)
    {
        var encoded = BlueTuskParameterEncoder.Encode(parameter);
        return BlueTuskValueDecoder.Decode(
            Field(encoded.TypeOid, encoded.FormatCode),
            encoded.Value)!;
    }

    private static BlueTuskDataReader CreateReader(
        BlueTuskFieldDescription field,
        ReadOnlyMemory<byte> value,
        BlueTuskTypeRegistry? types = null) =>
        new(
            new BlueTuskQueryResult(
            [
                new BlueTuskResultSet(
                    [field],
                    [new BlueTuskDataRow([value])],
                    "SELECT 1"),
            ]),
            connectionToClose: null,
            types ?? BlueTuskBuiltInTypes.CreateRegistry());

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

    private static byte[] Write(
        BlueTuskArrayCodec codec,
        BlueTuskTypeDescriptor type,
        object value)
    {
        Span<byte> destination = stackalloc byte[256];
        var writer = new BlueTuskWriter(destination);
        codec.Write(ref writer, value, BlueTuskDataFormat.Binary, type);
        return destination[..writer.WrittenCount].ToArray();
    }

    private sealed class TrackingInt32Codec : IBlueTuskCodec<int>
    {
        public int TypedReadCount { get; private set; }

        public int ObjectReadCount { get; private set; }

        public Type ClrType => typeof(int);

        public int ReadTyped(
            ref BlueTuskReader reader,
            BlueTuskDataFormat format,
            BlueTuskTypeDescriptor type)
        {
            TypedReadCount++;
            return reader.ReadInt32BigEndian();
        }

        public object Read(
            ref BlueTuskReader reader,
            BlueTuskDataFormat format,
            BlueTuskTypeDescriptor type)
        {
            ObjectReadCount++;
            return ReadTyped(ref reader, format, type);
        }

        public void WriteTyped(
            ref BlueTuskWriter writer,
            int value,
            BlueTuskDataFormat format,
            BlueTuskTypeDescriptor type) =>
            writer.WriteInt32BigEndian(value);

        public void Write(
            ref BlueTuskWriter writer,
            object? value,
            BlueTuskDataFormat format,
            BlueTuskTypeDescriptor type) =>
            WriteTyped(ref writer, (int)value!, format, type);
    }
}

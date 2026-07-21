using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskArrayCodecTests
{
    private static readonly BlueTuskTypeDescriptor Int4Array = new()
    {
        Id = new BlueTuskTypeId(1007),
        Schema = "pg_catalog",
        Name = "_int4",
        Kind = BlueTuskTypeKind.Array,
        ElementType = BlueTuskBuiltInTypes.Int4.Id,
    };

    [Fact]
    public void One_dimensional_arrays_round_trip_in_binary_and_text_formats()
    {
        var codec = new BlueTuskArrayCodec(BlueTuskBuiltInTypes.Int4, new BlueTuskInt32Codec());
        int[] expected = [int.MinValue, 0, int.MaxValue];

        AssertArrayEqual(expected, RoundTrip(codec, expected, BlueTuskDataFormat.Binary));
        AssertArrayEqual(expected, RoundTrip(codec, expected, BlueTuskDataFormat.Text));

        var binary = Write(codec, expected, BlueTuskDataFormat.Binary);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(binary));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(binary.AsSpan(4)));
        Assert.Equal(23U, BinaryPrimitives.ReadUInt32BigEndian(binary.AsSpan(8)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(binary.AsSpan(12)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(binary.AsSpan(16)));
    }

    [Fact]
    public void Multidimensional_arrays_preserve_shape_and_row_major_values()
    {
        var codec = new BlueTuskArrayCodec(BlueTuskBuiltInTypes.Int4, new BlueTuskInt32Codec());
        var expected = new int[,]
        {
            { 1, 2, 3 },
            { 4, 5, 6 },
        };

        AssertArrayEqual(expected, RoundTrip(codec, expected, BlueTuskDataFormat.Binary));
        AssertArrayEqual(expected, RoundTrip(codec, expected, BlueTuskDataFormat.Text));
    }

    [Fact]
    public void PostgreSql_lower_bounds_are_translated_to_zero_based_clr_bounds()
    {
        var codec = new BlueTuskArrayCodec(BlueTuskBuiltInTypes.Int4, new BlueTuskInt32Codec());
        var expected = Array.CreateInstance(typeof(int), [2], [-1]);
        expected.SetValue(5, -1);
        expected.SetValue(6, 0);

        var text = Encoding.UTF8.GetString(Write(codec, expected, BlueTuskDataFormat.Text));
        var actual = RoundTrip(codec, expected, BlueTuskDataFormat.Text);

        Assert.Equal("[0:1]={5,6}", text);
        AssertArrayEqual(expected, actual);
    }

    [Fact]
    public void Reference_type_arrays_preserve_nulls_and_text_escaping()
    {
        var textType = BlueTuskBuiltInTypes.Text;
        var arrayType = Int4Array with
        {
            Id = new BlueTuskTypeId(1009),
            Name = "_text",
            ElementType = textType.Id,
        };
        var codec = new BlueTuskArrayCodec(textType, new BlueTuskStringCodec());
        string?[] expected = ["", "NULL", "a,b", "a\\b", "a\"b", null, "snow 🐘"];

        AssertArrayEqual(expected, RoundTrip(codec, arrayType, expected, BlueTuskDataFormat.Binary));
        AssertArrayEqual(expected, RoundTrip(codec, arrayType, expected, BlueTuskDataFormat.Text));
        Assert.Equal(
            "{\"\",\"NULL\",\"a,b\",\"a\\\\b\",\"a\\\"b\",NULL,\"snow 🐘\"}",
            Encoding.UTF8.GetString(Write(codec, arrayType, expected, BlueTuskDataFormat.Text)));
    }

    [Fact]
    public void Element_delimiter_comes_from_the_catalogue_element_type()
    {
        var elementType = BlueTuskBuiltInTypes.Text with { Delimiter = ';' };
        var arrayType = Int4Array with { ElementType = elementType.Id };
        var codec = new BlueTuskArrayCodec(elementType, new BlueTuskStringCodec());
        var reader = new BlueTuskReader("{a,b;\"c;d\"}"u8);

        var actual = Assert.IsType<string[]>(codec.Read(ref reader, BlueTuskDataFormat.Text, arrayType));

        Assert.Equal(["a,b", "c;d"], actual);
    }

    [Fact]
    public void Null_value_type_element_is_rejected_without_silent_defaulting()
    {
        var bytes = Convert.FromHexString(
            "00000001" +
            "00000001" +
            "00000017" +
            "00000001" +
            "00000001" +
            "FFFFFFFF");
        var codec = new BlueTuskArrayCodec(BlueTuskBuiltInTypes.Int4, new BlueTuskInt32Codec());

        Assert.Throws<InvalidOperationException>(() => Read(codec, bytes, BlueTuskDataFormat.Binary));
    }

    [Theory]
    [InlineData("{{1,2},{3}}")]
    [InlineData("[1:3]={1,2}")]
    [InlineData("{\"unterminated}")]
    public void Malformed_text_arrays_are_rejected(string text)
    {
        var codec = new BlueTuskArrayCodec(BlueTuskBuiltInTypes.Int4, new BlueTuskInt32Codec());

        Assert.Throws<InvalidOperationException>(
            () => Read(codec, Encoding.UTF8.GetBytes(text), BlueTuskDataFormat.Text));
    }

    private static Array RoundTrip(
        BlueTuskArrayCodec codec,
        Array value,
        BlueTuskDataFormat format) => RoundTrip(codec, Int4Array, value, format);

    private static Array RoundTrip(
        BlueTuskArrayCodec codec,
        BlueTuskTypeDescriptor type,
        Array value,
        BlueTuskDataFormat format) => Read(codec, Write(codec, type, value, format), format, type);

    private static byte[] Write(
        BlueTuskArrayCodec codec,
        Array value,
        BlueTuskDataFormat format) => Write(codec, Int4Array, value, format);

    private static byte[] Write(
        BlueTuskArrayCodec codec,
        BlueTuskTypeDescriptor type,
        Array value,
        BlueTuskDataFormat format)
    {
        var destination = new byte[4096];
        var writer = new BlueTuskWriter(destination);
        codec.Write(ref writer, value, format, type);
        return destination.AsSpan(0, writer.WrittenCount).ToArray();
    }

    private static Array Read(
        BlueTuskArrayCodec codec,
        byte[] value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor? type = null)
    {
        var reader = new BlueTuskReader(value);
        return Assert.IsAssignableFrom<Array>(codec.Read(ref reader, format, type ?? Int4Array));
    }

    private static void AssertArrayEqual(Array expected, Array actual)
    {
        Assert.Equal(expected.Rank, actual.Rank);
        for (var dimension = 0; dimension < expected.Rank; dimension++)
        {
            Assert.Equal(expected.GetLength(dimension), actual.GetLength(dimension));
            Assert.Equal(expected.GetLowerBound(dimension), actual.GetLowerBound(dimension));
        }

        Assert.Equal(expected.Cast<object?>(), actual.Cast<object?>());
    }
}

using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskRangeCodecTests
{
    private static readonly BlueTuskTypeId IntRangeId = new(90_410);
    private static readonly BlueTuskTypeId IntMultirangeId = new(90_411);
    private static readonly BlueTuskTypeId IntRangeArrayId = new(90_412);
    private static readonly BlueTuskTypeId IntMultirangeArrayId = new(90_413);
    private static readonly BlueTuskTypeId NestedRangeId = new(90_414);
    private static readonly BlueTuskTypeId TextRangeId = new(90_420);

    [Fact]
    public void Catalogue_composes_range_multirange_and_dependent_array_codecs()
    {
        var registry = CreateRegistry();

        Assert.True(registry.TryGetCodec(IntRangeId, out var rangeCodec));
        Assert.IsType<BlueTuskRangeCodec<int>>(rangeCodec);
        Assert.True(registry.TryGetCodec(IntMultirangeId, out var multirangeCodec));
        Assert.IsType<BlueTuskMultirangeCodec<int>>(multirangeCodec);
        Assert.True(registry.TryGetCodec(IntRangeArrayId, out var rangeArrayCodec));
        Assert.Equal(typeof(BlueTuskRange<int>[]), rangeArrayCodec!.ClrType);
        Assert.True(registry.TryGetCodec(IntMultirangeArrayId, out var multirangeArrayCodec));
        Assert.Equal(typeof(BlueTuskMultirange<int>[]), multirangeArrayCodec!.ClrType);

        Assert.True(registry.TryGetType(typeof(BlueTuskRange<int>), out var rangeType, out _));
        Assert.Equal(IntRangeId, rangeType!.Id);
        Assert.True(registry.TryGetType(typeof(BlueTuskMultirange<int>), out var multirangeType, out _));
        Assert.Equal(IntMultirangeId, multirangeType!.Id);
    }

    [Fact]
    public void Jit_catalogue_composes_a_nested_range_without_recursive_static_instantiation()
    {
        var registry = CreateRegistry();

        Assert.True(registry.TryGetCodec(NestedRangeId, out var codec));
        Assert.IsType<BlueTuskRangeCodec<BlueTuskRange<int>>>(codec);
    }

    [Fact]
    public void Finite_range_round_trips_exact_binary_flags_and_text()
    {
        var (codec, type) = GetRangeCodec<int>(CreateRegistry(), IntRangeId);
        var expected = new BlueTuskRange<int>(
            BlueTuskRangeBound.Inclusive(1),
            BlueTuskRangeBound.Exclusive(5));

        Assert.Equal(expected, RoundTrip(codec, type, expected, BlueTuskDataFormat.Binary));
        Assert.Equal(expected, RoundTrip(codec, type, expected, BlueTuskDataFormat.Text));

        Assert.Equal("[1,5)", Encoding.UTF8.GetString(Write(codec, type, expected, BlueTuskDataFormat.Text)));
        Assert.Equal(
            "0200000004000000010000000400000005",
            Convert.ToHexString(Write(codec, type, expected, BlueTuskDataFormat.Binary)));
    }

    [Fact]
    public void Empty_and_infinite_ranges_preserve_distinct_states()
    {
        var (codec, type) = GetRangeCodec<int>(CreateRegistry(), IntRangeId);
        var empty = BlueTuskRange.Empty<int>();
        var upperBounded = new BlueTuskRange<int>(
            BlueTuskRangeBound.Unbounded<int>(),
            BlueTuskRangeBound.Inclusive(10));

        Assert.Equal(empty, RoundTrip(codec, type, empty, BlueTuskDataFormat.Binary));
        Assert.Equal("empty", Encoding.UTF8.GetString(Write(codec, type, empty, BlueTuskDataFormat.Text)));
        Assert.Equal("01", Convert.ToHexString(Write(codec, type, empty, BlueTuskDataFormat.Binary)));

        Assert.Equal(upperBounded, RoundTrip(codec, type, upperBounded, BlueTuskDataFormat.Binary));
        Assert.Equal(upperBounded, RoundTrip(codec, type, upperBounded, BlueTuskDataFormat.Text));
        Assert.Equal("(,10]", Encoding.UTF8.GetString(Write(codec, type, upperBounded, BlueTuskDataFormat.Text)));
        Assert.Equal(
            BlueTuskRange.Unbounded<int>(),
            ReadText(codec, type, "[,]"));
    }

    [Fact]
    public void Text_boundaries_round_trip_postgresql_quotes_and_escapes()
    {
        var (codec, type) = GetRangeCodec<string>(CreateRegistry(), TextRangeId);
        var expected = new BlueTuskRange<string>(
            BlueTuskRangeBound.Inclusive("a,b"),
            BlueTuskRangeBound.Exclusive("x\"y\\z"));

        var text = Encoding.UTF8.GetString(Write(codec, type, expected, BlueTuskDataFormat.Text));
        var actual = ReadText(codec, type, text);

        Assert.Equal("[\"a,b\",\"x\"\"y\\\\z\")", text);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Multirange_round_trips_binary_text_and_empty_values()
    {
        var registry = CreateRegistry();
        var type = Assert.Single(registry.Types, candidate => candidate.Id == IntMultirangeId);
        Assert.True(registry.TryGetCodec(type.Id, out var registered));
        var codec = Assert.IsType<BlueTuskMultirangeCodec<int>>(registered);
        var expected = new BlueTuskMultirange<int>(
        [
            new BlueTuskRange<int>(1, 5),
            new BlueTuskRange<int>(10, 20),
        ]);

        Assert.Equal(expected, RoundTrip(codec, type, expected, BlueTuskDataFormat.Binary));
        Assert.Equal(expected, RoundTrip(codec, type, expected, BlueTuskDataFormat.Text));
        Assert.Equal(
            "{[1,5),[10,20)}",
            Encoding.UTF8.GetString(Write(codec, type, expected, BlueTuskDataFormat.Text)));
        var binary = Write(codec, type, expected, BlueTuskDataFormat.Binary);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(binary));
        Assert.Equal(17, BinaryPrimitives.ReadInt32BigEndian(binary.AsSpan(4)));

        var empty = BlueTuskMultirange.Empty<int>();
        Assert.Equal(empty, RoundTrip(codec, type, empty, BlueTuskDataFormat.Binary));
        Assert.Equal("{}", Encoding.UTF8.GetString(Write(codec, type, empty, BlueTuskDataFormat.Text)));
    }

    [Theory]
    [InlineData("[1,2")]
    [InlineData("[\"unterminated,2)")]
    [InlineData("[1,2) trailing")]
    public void Malformed_range_text_is_rejected(string text)
    {
        var (codec, type) = GetRangeCodec<int>(CreateRegistry(), IntRangeId);

        Assert.Throws<InvalidOperationException>(() => ReadText(codec, type, text));
    }

    [Theory]
    [InlineData("{[1,2)")]
    [InlineData("{[1,2),}")]
    [InlineData("{not-a-range}")]
    public void Malformed_multirange_text_is_rejected(string text)
    {
        var registry = CreateRegistry();
        var type = Assert.Single(registry.Types, candidate => candidate.Id == IntMultirangeId);
        Assert.True(registry.TryGetCodec(type.Id, out var registered));
        var codec = Assert.IsType<BlueTuskMultirangeCodec<int>>(registered);

        Assert.Throws<InvalidOperationException>(() => ReadText(codec, type, text));
    }

    [Fact]
    public void Unsupported_binary_range_flags_are_rejected()
    {
        var (codec, type) = GetRangeCodec<int>(CreateRegistry(), IntRangeId);

        Assert.Throws<InvalidOperationException>(
            () => Read(codec, type, [0x20], BlueTuskDataFormat.Binary));
    }

    private static BlueTuskTypeRegistry CreateRegistry() =>
        BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = IntRangeId,
                Schema = "app",
                Name = "int_span",
                PostgreSqlKind = 'r',
                PostgreSqlCategory = 'R',
                ArrayType = IntRangeArrayId,
                RangeSubtype = BlueTuskBuiltInTypes.Int4.Id,
                RangeType = IntRangeId,
                MultirangeType = IntMultirangeId,
            },
            new BlueTuskCatalogueType
            {
                Id = IntMultirangeId,
                Schema = "app",
                Name = "int_span_multi",
                PostgreSqlKind = 'm',
                PostgreSqlCategory = 'R',
                ArrayType = IntMultirangeArrayId,
                RangeSubtype = BlueTuskBuiltInTypes.Int4.Id,
                RangeType = IntRangeId,
                MultirangeType = IntMultirangeId,
            },
            new BlueTuskCatalogueType
            {
                Id = IntRangeArrayId,
                Schema = "app",
                Name = "_int_span",
                PostgreSqlKind = 'b',
                PostgreSqlCategory = 'A',
                ElementType = IntRangeId,
            },
            new BlueTuskCatalogueType
            {
                Id = IntMultirangeArrayId,
                Schema = "app",
                Name = "_int_span_multi",
                PostgreSqlKind = 'b',
                PostgreSqlCategory = 'A',
                ElementType = IntMultirangeId,
            },
            new BlueTuskCatalogueType
            {
                Id = TextRangeId,
                Schema = "app",
                Name = "text_span",
                PostgreSqlKind = 'r',
                PostgreSqlCategory = 'R',
                RangeSubtype = BlueTuskBuiltInTypes.Text.Id,
                RangeType = TextRangeId,
            },
            new BlueTuskCatalogueType
            {
                Id = NestedRangeId,
                Schema = "app",
                Name = "nested_int_span",
                PostgreSqlKind = 'r',
                PostgreSqlCategory = 'R',
                RangeSubtype = IntRangeId,
                RangeType = NestedRangeId,
            },
        ]);

    private static (BlueTuskRangeCodec<T> Codec, BlueTuskTypeDescriptor Type) GetRangeCodec<T>(
        BlueTuskTypeRegistry registry,
        BlueTuskTypeId id)
    {
        var type = Assert.Single(registry.Types, candidate => candidate.Id == id);
        Assert.True(registry.TryGetCodec(id, out var registered));
        return (Assert.IsType<BlueTuskRangeCodec<T>>(registered), type);
    }

    private static T RoundTrip<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        BlueTuskDataFormat format) =>
        Read(codec, type, Write(codec, type, value, format), format);

    private static byte[] Write<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        BlueTuskDataFormat format)
    {
        var destination = new byte[4096];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, format, type);
        return destination.AsSpan(0, writer.WrittenCount).ToArray();
    }

    private static T Read<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        byte[] value,
        BlueTuskDataFormat format)
    {
        var reader = new BlueTuskReader(value);
        return codec.ReadTyped(ref reader, format, type);
    }

    private static T ReadText<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        string text) =>
        Read(codec, type, Encoding.UTF8.GetBytes(text), BlueTuskDataFormat.Text);
}

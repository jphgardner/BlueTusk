namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskOpaqueCatalogueCodecTests
{
    [Fact]
    public void Opaque_value_preserves_format_and_defensively_copies_bytes()
    {
        byte[] source = [1, 2, 3, 4];
        var value = new BlueTuskNDistinctStatistics(BlueTuskDataFormat.Binary, source);
        source[0] = byte.MaxValue;

        Assert.Equal(BlueTuskDataFormat.Binary, value.Format);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, value.Data.ToArray());
        Assert.Equal(
            value,
            new BlueTuskNDistinctStatistics(
                BlueTuskDataFormat.Binary,
                new byte[] { 1, 2, 3, 4 }));
        Assert.NotEqual(
            value,
            new BlueTuskNDistinctStatistics(
                BlueTuskDataFormat.Text,
                new byte[] { 1, 2, 3, 4 }));
        Assert.False(
            value.Equals(
                new BlueTuskDependencyStatistics(
                    BlueTuskDataFormat.Binary,
                    new byte[] { 1, 2, 3, 4 })));
    }

    [Fact]
    public void Opaque_codec_decodes_raw_bytes_but_rejects_input()
    {
        var codec = new BlueTuskNDistinctStatisticsCodec();
        var reader = new BlueTuskReader(new byte[] { 0, 1, 2, 255 });
        var actual = codec.ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.PgNDistinct);

        Assert.Equal(
            new BlueTuskNDistinctStatistics(
                BlueTuskDataFormat.Binary,
                new byte[] { 0, 1, 2, 255 }),
            actual);

        Assert.Throws<NotSupportedException>(
            () => Write(
                codec,
                actual,
                BlueTuskDataFormat.Binary));
    }

    [Fact]
    public void Opaque_catalogue_types_have_distinct_inference_types()
    {
        var registry = BlueTuskBuiltInTypes.CreateRegistry();

        AssertType<BlueTuskNDistinctStatistics>(registry, BlueTuskBuiltInTypes.PgNDistinct);
        AssertType<BlueTuskDependencyStatistics>(registry, BlueTuskBuiltInTypes.PgDependencies);
        AssertType<BlueTuskMostCommonValueStatistics>(registry, BlueTuskBuiltInTypes.PgMcvList);
        AssertType<BlueTuskBrinBloomSummary>(registry, BlueTuskBuiltInTypes.PgBrinBloomSummary);
        AssertType<BlueTuskBrinMinMaxMultiSummary>(
            registry,
            BlueTuskBuiltInTypes.PgBrinMinMaxMultiSummary);
    }

    private static void Write(
        BlueTuskNDistinctStatisticsCodec codec,
        BlueTuskNDistinctStatistics value,
        BlueTuskDataFormat format)
    {
        Span<byte> destination = stackalloc byte[16];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(
            ref writer,
            value,
            format,
            BlueTuskBuiltInTypes.PgNDistinct);
    }

    private static void AssertType<T>(
        BlueTuskTypeRegistry registry,
        BlueTuskTypeDescriptor expected)
    {
        Assert.True(registry.TryGetType(typeof(T), out var type, out var codec));
        Assert.Equal(expected.Id, type!.Id);
        Assert.Equal(typeof(T), codec!.ClrType);
    }
}

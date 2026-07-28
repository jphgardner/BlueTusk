namespace BlueTusk.TypeSystem;

public abstract class BlueTuskOpaqueCatalogueCodec<T> : BlueTuskCodec<T>
    where T : BlueTuskOpaqueCatalogueValue
{
    public override T ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        Create(format, reader.ReadRemainingBytes().ToArray());

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        T value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        throw new NotSupportedException(
            $"PostgreSQL does not accept input values for {type.QualifiedName}.");

    protected abstract T Create(
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> data);
}

public sealed class BlueTuskNDistinctStatisticsCodec :
    BlueTuskOpaqueCatalogueCodec<BlueTuskNDistinctStatistics>
{
    protected override BlueTuskNDistinctStatistics Create(
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> data) => new(format, data);
}

public sealed class BlueTuskDependencyStatisticsCodec :
    BlueTuskOpaqueCatalogueCodec<BlueTuskDependencyStatistics>
{
    protected override BlueTuskDependencyStatistics Create(
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> data) => new(format, data);
}

public sealed class BlueTuskMostCommonValueStatisticsCodec :
    BlueTuskOpaqueCatalogueCodec<BlueTuskMostCommonValueStatistics>
{
    protected override BlueTuskMostCommonValueStatistics Create(
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> data) => new(format, data);
}

public sealed class BlueTuskBrinBloomSummaryCodec :
    BlueTuskOpaqueCatalogueCodec<BlueTuskBrinBloomSummary>
{
    protected override BlueTuskBrinBloomSummary Create(
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> data) => new(format, data);
}

public sealed class BlueTuskBrinMinMaxMultiSummaryCodec :
    BlueTuskOpaqueCatalogueCodec<BlueTuskBrinMinMaxMultiSummary>
{
    protected override BlueTuskBrinMinMaxMultiSummary Create(
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> data) => new(format, data);
}

namespace BlueTusk.TypeSystem;

/// <summary>
/// An immutable, format-preserving PostgreSQL system-catalogue value whose wire
/// representation is intentionally opaque to clients.
/// </summary>
public abstract class BlueTuskOpaqueCatalogueValue : IEquatable<BlueTuskOpaqueCatalogueValue>
{
    private readonly byte[] _data;

    protected BlueTuskOpaqueCatalogueValue(
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> data)
    {
        if (format is not (BlueTuskDataFormat.Text or BlueTuskDataFormat.Binary))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        Format = format;
        _data = data.ToArray();
    }

    public BlueTuskDataFormat Format { get; }

    public ReadOnlyMemory<byte> Data => _data;

    public bool Equals(BlueTuskOpaqueCatalogueValue? other) =>
        other is not null &&
        GetType() == other.GetType() &&
        Format == other.Format &&
        _data.AsSpan().SequenceEqual(other._data);

    public override bool Equals(object? obj) =>
        obj is BlueTuskOpaqueCatalogueValue other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GetType());
        hash.Add(Format);
        foreach (var value in _data)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

/// <summary>An opaque PostgreSQL <c>pg_ndistinct</c> statistics value.</summary>
public sealed class BlueTuskNDistinctStatistics : BlueTuskOpaqueCatalogueValue
{
    public BlueTuskNDistinctStatistics(
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> data)
        : base(format, data)
    {
    }
}

/// <summary>An opaque PostgreSQL <c>pg_dependencies</c> statistics value.</summary>
public sealed class BlueTuskDependencyStatistics : BlueTuskOpaqueCatalogueValue
{
    public BlueTuskDependencyStatistics(
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> data)
        : base(format, data)
    {
    }
}

/// <summary>An opaque PostgreSQL <c>pg_mcv_list</c> statistics value.</summary>
public sealed class BlueTuskMostCommonValueStatistics : BlueTuskOpaqueCatalogueValue
{
    public BlueTuskMostCommonValueStatistics(
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> data)
        : base(format, data)
    {
    }
}

/// <summary>An opaque PostgreSQL <c>pg_brin_bloom_summary</c> value.</summary>
public sealed class BlueTuskBrinBloomSummary : BlueTuskOpaqueCatalogueValue
{
    public BlueTuskBrinBloomSummary(
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> data)
        : base(format, data)
    {
    }
}

/// <summary>An opaque PostgreSQL <c>pg_brin_minmax_multi_summary</c> value.</summary>
public sealed class BlueTuskBrinMinMaxMultiSummary : BlueTuskOpaqueCatalogueValue
{
    public BlueTuskBrinMinMaxMultiSummary(
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> data)
        : base(format, data)
    {
    }
}

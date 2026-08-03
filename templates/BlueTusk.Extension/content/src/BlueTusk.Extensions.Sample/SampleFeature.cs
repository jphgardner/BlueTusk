using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.Sample;

/// <summary>Describes the immutable extension registration on a built data source.</summary>
public sealed record SampleFeature(BlueTuskTypeName TypeName)
{
    public const string RegistryName = "bluetusk.extensions." + "sample_type";
}

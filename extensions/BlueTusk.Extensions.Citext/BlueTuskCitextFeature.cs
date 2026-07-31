using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.Citext;

/// <summary>Describes the citext registration carried by a built data source.</summary>
public sealed record BlueTuskCitextFeature(BlueTuskTypeName TypeName)
{
    public const string RegistryName = "bluetusk.extensions.citext";
}

using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.HStore;

/// <summary>Describes the hstore registration carried by a built data source.</summary>
public sealed record BlueTuskHStoreFeature(BlueTuskTypeName TypeName)
{
    public const string RegistryName = "bluetusk.extensions.hstore";
}

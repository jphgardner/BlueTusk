using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.LTree;

/// <summary>Describes the ltree registrations carried by a built data source.</summary>
public sealed record BlueTuskLTreeFeature(
    BlueTuskTypeName LTreeTypeName,
    BlueTuskTypeName LQueryTypeName,
    BlueTuskTypeName LTxtQueryTypeName)
{
    public const string RegistryName = "bluetusk.extensions.ltree";
}

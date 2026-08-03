using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.PostGIS;

/// <summary>Describes the PostGIS spatial registrations carried by a built data source.</summary>
public sealed record BlueTuskPostGisFeature(
    BlueTuskTypeName GeometryTypeName,
    BlueTuskTypeName GeographyTypeName)
{
    public const string RegistryName = "bluetusk.extensions.postgis";
}

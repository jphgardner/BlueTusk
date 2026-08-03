using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.PostGIS;

/// <summary>Registers schema-local PostGIS geometry and geography types.</summary>
public sealed class BlueTuskPostGisPlugin : IBlueTuskPlugin
{
    public BlueTuskPostGisPlugin(string schema = "public")
    {
        GeometryTypeName = new BlueTuskTypeName(schema, "geometry");
        GeographyTypeName = new BlueTuskTypeName(schema, "geography");
    }

    public BlueTuskTypeName GeometryTypeName { get; }

    public BlueTuskTypeName GeographyTypeName { get; }

    public void Configure(IBlueTuskPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Types.Register(
            GeometryTypeName.Schema,
            GeometryTypeName.Name,
            new BlueTuskGeometryCodec());
        context.Types.Register(
            GeographyTypeName.Schema,
            GeographyTypeName.Name,
            new BlueTuskGeographyCodec());
        context.Features.Register(
            BlueTuskPostGisFeature.RegistryName,
            new BlueTuskPostGisFeature(GeometryTypeName, GeographyTypeName));
    }
}

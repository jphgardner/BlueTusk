using BlueTusk.Data;

namespace BlueTusk.Extensions.PostGIS;

public static class BlueTuskPostGisDataSourceBuilderExtensions
{
    /// <summary>Adds PostGIS geometry and geography transport support to a data source.</summary>
    public static BlueTuskDataSourceBuilder UsePostGis(
        this BlueTuskDataSourceBuilder builder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UsePlugin(new BlueTuskPostGisPlugin(schema));
    }
}

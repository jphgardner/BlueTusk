using BlueTusk.Data;

namespace BlueTusk.Extensions.TimescaleDB;

public static class BlueTuskTimescaleDbDataSourceBuilderExtensions
{
    /// <summary>Adds TimescaleDB hypertable and retention behavior to a data source.</summary>
    public static BlueTuskDataSourceBuilder UseTimescaleDb(
        this BlueTuskDataSourceBuilder builder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UsePlugin(new BlueTuskTimescaleDbPlugin(schema));
    }
}

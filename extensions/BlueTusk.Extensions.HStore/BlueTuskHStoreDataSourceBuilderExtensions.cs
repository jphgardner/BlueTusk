using BlueTusk.Data;

namespace BlueTusk.Extensions.HStore;

public static class BlueTuskHStoreDataSourceBuilderExtensions
{
    /// <summary>Adds PostgreSQL hstore type support to a data source.</summary>
    public static BlueTuskDataSourceBuilder UseHStore(
        this BlueTuskDataSourceBuilder builder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UsePlugin(new BlueTuskHStorePlugin(schema));
    }
}

using BlueTusk.Data;

namespace BlueTusk.Extensions.PgTrgm;

public static class BlueTuskPgTrgmDataSourceBuilderExtensions
{
    /// <summary>Adds PostgreSQL pg_trgm behavior to a data source.</summary>
    public static BlueTuskDataSourceBuilder UsePgTrgm(
        this BlueTuskDataSourceBuilder builder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UsePlugin(new BlueTuskPgTrgmPlugin(schema));
    }
}

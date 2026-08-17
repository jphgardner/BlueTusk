using BlueTusk.Data;

namespace BlueTusk.Extensions.PgDurable;

public static class BlueTuskPgDurableDataSourceBuilderExtensions
{
    /// <summary>Adds PostgreSQL pg_durable behavior to a data source.</summary>
    public static BlueTuskDataSourceBuilder UsePgDurable(this BlueTuskDataSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UsePlugin(new BlueTuskPgDurablePlugin());
    }
}

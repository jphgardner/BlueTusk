using BlueTusk.Data;

namespace BlueTusk.Extensions.LTree;

public static class BlueTuskLTreeDataSourceBuilderExtensions
{
    /// <summary>Adds PostgreSQL ltree, lquery, and ltxtquery support to a data source.</summary>
    public static BlueTuskDataSourceBuilder UseLTree(
        this BlueTuskDataSourceBuilder builder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UsePlugin(new BlueTuskLTreePlugin(schema));
    }
}

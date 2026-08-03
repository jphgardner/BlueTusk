using BlueTusk.Data;

namespace BlueTusk.Extensions.Citext;

public static class BlueTuskCitextDataSourceBuilderExtensions
{
    /// <summary>Adds PostgreSQL citext type support to a data source.</summary>
    public static BlueTuskDataSourceBuilder UseCitext(
        this BlueTuskDataSourceBuilder builder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UsePlugin(new BlueTuskCitextPlugin(schema));
    }
}

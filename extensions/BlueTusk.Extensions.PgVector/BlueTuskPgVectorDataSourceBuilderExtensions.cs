using BlueTusk.Data;

namespace BlueTusk.Extensions.PgVector;

public static class BlueTuskPgVectorDataSourceBuilderExtensions
{
    /// <summary>Adds PostgreSQL pgvector dense, half-precision, and sparse-vector support.</summary>
    public static BlueTuskDataSourceBuilder UsePgVector(
        this BlueTuskDataSourceBuilder builder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UsePlugin(new BlueTuskPgVectorPlugin(schema));
    }
}

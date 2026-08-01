using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.PgVector;

/// <summary>Describes the pgvector registration carried by a built data source.</summary>
public sealed record BlueTuskPgVectorFeature(BlueTuskTypeName TypeName)
{
    public const string RegistryName = "bluetusk.extensions.pgvector";
}

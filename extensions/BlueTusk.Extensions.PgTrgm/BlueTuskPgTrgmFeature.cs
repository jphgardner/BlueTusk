namespace BlueTusk.Extensions.PgTrgm;

/// <summary>Describes the pg_trgm installation carried by a built data source.</summary>
public sealed record BlueTuskPgTrgmFeature(string Schema)
{
    public const string RegistryName = "bluetusk.extensions.pg_trgm";
}

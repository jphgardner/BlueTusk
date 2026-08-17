namespace BlueTusk.Extensions.PgDurable;

/// <summary>Describes pg_durable behavior carried by a built data source.</summary>
public sealed record BlueTuskPgDurableFeature
{
    public const string RegistryName = "bluetusk.extensions.pg_durable";

    public const string ExtensionName = "pg_durable";

    public const string Schema = "df";

    public const string MinimumSupportedVersion = "0.2.5";
}

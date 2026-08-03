namespace BlueTusk.Extensions.TimescaleDB;

/// <summary>Describes the TimescaleDB installation carried by a built data source.</summary>
public sealed record BlueTuskTimescaleDbFeature(string Schema)
{
    public const string RegistryName = "bluetusk.extensions.timescaledb";
}

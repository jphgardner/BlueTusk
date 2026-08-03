namespace BlueTusk.Extensions.PgTrgm;

/// <summary>Registers schema-local PostgreSQL pg_trgm behavior.</summary>
public sealed class BlueTuskPgTrgmPlugin : IBlueTuskPlugin
{
    public BlueTuskPgTrgmPlugin(string schema = "public")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        Schema = schema;
    }

    public string Schema { get; }

    public void Configure(IBlueTuskPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Features.Register(
            BlueTuskPgTrgmFeature.RegistryName,
            new BlueTuskPgTrgmFeature(Schema));
    }
}

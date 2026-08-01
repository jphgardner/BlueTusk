namespace BlueTusk.Extensions.TimescaleDB;

/// <summary>Registers schema-local TimescaleDB behavior.</summary>
public sealed class BlueTuskTimescaleDbPlugin : IBlueTuskPlugin
{
    public BlueTuskTimescaleDbPlugin(string schema = "public")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        Schema = schema;
    }

    public string Schema { get; }

    public void Configure(IBlueTuskPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Features.Register(
            BlueTuskTimescaleDbFeature.RegistryName,
            new BlueTuskTimescaleDbFeature(Schema));
    }
}

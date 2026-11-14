namespace BlueTusk.Extensions.PgDurable;

/// <summary>Registers PostgreSQL pg_durable behavior.</summary>
public sealed class BlueTuskPgDurablePlugin : IBlueTuskPlugin
{
    public void Configure(IBlueTuskPluginContext context)
    {
        BlueTuskGuard.NotNull(context);
        context.Features.Register(
            BlueTuskPgDurableFeature.RegistryName,
            new BlueTuskPgDurableFeature());
    }
}

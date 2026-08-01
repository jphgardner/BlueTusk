using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.HStore;

/// <summary>Registers a schema-local PostgreSQL hstore type and feature descriptor.</summary>
public sealed class BlueTuskHStorePlugin : IBlueTuskPlugin
{
    public BlueTuskHStorePlugin(string schema = "public")
    {
        TypeName = new BlueTuskTypeName(schema, "hstore");
    }

    public BlueTuskTypeName TypeName { get; }

    public void Configure(IBlueTuskPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Types.Register(TypeName.Schema, TypeName.Name, new BlueTuskHStoreCodec());
        context.Features.Register(
            BlueTuskHStoreFeature.RegistryName,
            new BlueTuskHStoreFeature(TypeName));
    }
}

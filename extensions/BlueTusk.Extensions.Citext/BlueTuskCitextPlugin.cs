using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.Citext;

/// <summary>Registers a schema-local PostgreSQL citext type and its immutable feature descriptor.</summary>
public sealed class BlueTuskCitextPlugin : IBlueTuskPlugin
{
    public BlueTuskCitextPlugin(string schema = "public")
    {
        TypeName = new BlueTuskTypeName(schema, "citext");
    }

    public BlueTuskTypeName TypeName { get; }

    public void Configure(IBlueTuskPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Types.Register(TypeName.Schema, TypeName.Name, new BlueTuskCitextCodec());
        context.Features.Register(
            BlueTuskCitextFeature.RegistryName,
            new BlueTuskCitextFeature(TypeName));
    }
}

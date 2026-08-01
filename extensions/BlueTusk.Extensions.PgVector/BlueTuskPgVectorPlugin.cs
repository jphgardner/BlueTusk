using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.PgVector;

/// <summary>Registers a schema-local pgvector <c>vector</c> type and feature descriptor.</summary>
public sealed class BlueTuskPgVectorPlugin : IBlueTuskPlugin
{
    public BlueTuskPgVectorPlugin(string schema = "public")
    {
        TypeName = new BlueTuskTypeName(schema, "vector");
    }

    public BlueTuskTypeName TypeName { get; }

    public void Configure(IBlueTuskPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Types.Register(TypeName.Schema, TypeName.Name, new BlueTuskVectorCodec());
        context.Features.Register(
            BlueTuskPgVectorFeature.RegistryName,
            new BlueTuskPgVectorFeature(TypeName));
    }
}

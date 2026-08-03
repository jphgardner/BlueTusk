using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.PgVector;

/// <summary>Registers a schema-local pgvector <c>vector</c> type and feature descriptor.</summary>
public sealed class BlueTuskPgVectorPlugin : IBlueTuskPlugin
{
    public BlueTuskPgVectorPlugin(string schema = "public")
    {
        TypeName = new BlueTuskTypeName(schema, "vector");
        HalfVectorTypeName = new BlueTuskTypeName(schema, "halfvec");
        SparseVectorTypeName = new BlueTuskTypeName(schema, "sparsevec");
    }

    public BlueTuskTypeName TypeName { get; }

    public BlueTuskTypeName HalfVectorTypeName { get; }

    public BlueTuskTypeName SparseVectorTypeName { get; }

    public void Configure(IBlueTuskPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Types.Register(TypeName.Schema, TypeName.Name, new BlueTuskVectorCodec());
        context.Types.Register(
            HalfVectorTypeName.Schema,
            HalfVectorTypeName.Name,
            new BlueTuskHalfVectorCodec());
        context.Types.Register(
            SparseVectorTypeName.Schema,
            SparseVectorTypeName.Name,
            new BlueTuskSparseVectorCodec());
        context.Features.Register(
            BlueTuskPgVectorFeature.RegistryName,
            new BlueTuskPgVectorFeature(TypeName, HalfVectorTypeName, SparseVectorTypeName));
    }
}

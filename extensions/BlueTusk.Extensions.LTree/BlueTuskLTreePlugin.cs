using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.LTree;

/// <summary>Registers schema-local PostgreSQL ltree, lquery, and ltxtquery types.</summary>
public sealed class BlueTuskLTreePlugin : IBlueTuskPlugin
{
    public BlueTuskLTreePlugin(string schema = "public")
    {
        LTreeTypeName = new BlueTuskTypeName(schema, "ltree");
        LQueryTypeName = new BlueTuskTypeName(schema, "lquery");
        LTxtQueryTypeName = new BlueTuskTypeName(schema, "ltxtquery");
    }

    public BlueTuskTypeName LTreeTypeName { get; }

    public BlueTuskTypeName LQueryTypeName { get; }

    public BlueTuskTypeName LTxtQueryTypeName { get; }

    public void Configure(IBlueTuskPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Types.Register(LTreeTypeName.Schema, LTreeTypeName.Name, new BlueTuskLTreeCodec());
        context.Types.Register(LQueryTypeName.Schema, LQueryTypeName.Name, new BlueTuskLQueryCodec());
        context.Types.Register(
            LTxtQueryTypeName.Schema,
            LTxtQueryTypeName.Name,
            new BlueTuskLTxtQueryCodec());
        context.Features.Register(
            BlueTuskLTreeFeature.RegistryName,
            new BlueTuskLTreeFeature(LTreeTypeName, LQueryTypeName, LTxtQueryTypeName));
    }
}

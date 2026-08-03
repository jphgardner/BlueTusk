using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.Sample;

/// <summary>Registers PostgreSQL sample_type without adding extension code to core packages.</summary>
public sealed class SamplePlugin : IBlueTuskPlugin
{
    public SamplePlugin(string schema = "public")
    {
        TypeName = new BlueTuskTypeName(schema, "sample_type");
    }

    public BlueTuskTypeName TypeName { get; }

    public void Configure(IBlueTuskPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Types.Register(TypeName.Schema, TypeName.Name, new SampleCodec());
        context.Features.Register(
            SampleFeature.RegistryName,
            new SampleFeature(TypeName));
    }
}

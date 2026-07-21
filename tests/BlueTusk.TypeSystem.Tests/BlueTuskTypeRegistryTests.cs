using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskTypeRegistryTests
{
    [Fact]
    public void Initial_registry_resolves_int4_by_catalogue_oid()
    {
        var registry = BlueTuskBuiltInTypes.CreateInitialRegistry();

        Assert.True(registry.TryGetType(new BlueTuskTypeId(23), out var type));
        Assert.Equal("pg_catalog.int4", type!.QualifiedName);
        Assert.True(registry.TryGetCodec(type.Id, out var codec));
        Assert.IsType<BlueTuskInt32Codec>(codec);
    }

    [Fact]
    public void Unknown_text_values_remain_accessible()
    {
        var type = new BlueTuskTypeDescriptor
        {
            Id = new BlueTuskTypeId(90001),
            Schema = "future",
            Name = "new_type",
            Kind = BlueTuskTypeKind.Unknown,
        };
        var value = new BlueTuskUnknownValue(type, BlueTuskDataFormat.Text, Encoding.UTF8.GetBytes("future-value"));

        Assert.Equal("future-value", value.GetText());
    }
}


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

    [Theory]
    [InlineData(16, "bool", typeof(bool), typeof(BlueTuskBooleanCodec))]
    [InlineData(17, "bytea", typeof(byte[]), typeof(BlueTuskByteArrayCodec))]
    [InlineData(20, "int8", typeof(long), typeof(BlueTuskInt64Codec))]
    [InlineData(21, "int2", typeof(short), typeof(BlueTuskInt16Codec))]
    [InlineData(25, "text", typeof(string), typeof(BlueTuskStringCodec))]
    [InlineData(26, "oid", typeof(uint), typeof(BlueTuskUInt32Codec))]
    [InlineData(700, "float4", typeof(float), typeof(BlueTuskSingleCodec))]
    [InlineData(701, "float8", typeof(double), typeof(BlueTuskDoubleCodec))]
    [InlineData(2950, "uuid", typeof(Guid), typeof(BlueTuskGuidCodec))]
    [InlineData(3802, "jsonb", typeof(string), typeof(BlueTuskJsonbCodec))]
    public void Registry_resolves_core_scalar_codecs(
        uint oid,
        string name,
        Type clrType,
        Type codecType)
    {
        var registry = BlueTuskBuiltInTypes.CreateRegistry();

        Assert.True(registry.TryGetType(new BlueTuskTypeId(oid), out var type));
        Assert.Equal(name, type!.Name);
        Assert.True(registry.TryGetCodec(type.Id, out var codec));
        Assert.Equal(clrType, codec!.ClrType);
        Assert.IsType(codecType, codec);
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

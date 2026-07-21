using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskRecordCodecTests
{
    private static readonly BlueTuskTypeDescriptor AddressType = new()
    {
        Id = new BlueTuskTypeId(90_400),
        Schema = "app",
        Name = "address",
        Kind = BlueTuskTypeKind.Composite,
        CompositeFields =
        [
            new BlueTuskCompositeField
            {
                Position = 1,
                Name = "house_number",
                Type = BlueTuskBuiltInTypes.Int4.Id,
            },
            new BlueTuskCompositeField
            {
                Position = 2,
                Name = "street",
                Type = BlueTuskBuiltInTypes.Text.Id,
            },
            new BlueTuskCompositeField
            {
                Position = 3,
                Name = "note",
                Type = BlueTuskBuiltInTypes.Text.Id,
            },
        ],
    };

    [Fact]
    public void Named_composite_round_trips_binary_and_text_with_nulls_and_escaping()
    {
        var codec = CreateNamedCodec();
        var value = new BlueTuskRecord(
        [
            new BlueTuskRecordField("house_number", BlueTuskBuiltInTypes.Int4, 42),
            new BlueTuskRecordField("street", BlueTuskBuiltInTypes.Text, "Main, \\\"Road\\\" 🐘"),
            new BlueTuskRecordField("note", BlueTuskBuiltInTypes.Text, null),
        ]);

        AssertRecordEqual(value, RoundTrip(codec, value, BlueTuskDataFormat.Binary));
        AssertRecordEqual(value, RoundTrip(codec, value, BlueTuskDataFormat.Text));

        var text = Encoding.UTF8.GetString(Write(codec, value, BlueTuskDataFormat.Text));
        var doubledSlash = new string('\\', 2);
        Assert.Equal($"(42,\"Main, {doubledSlash}\"\"Road{doubledSlash}\"\" 🐘\",)", text);
        var binary = Write(codec, value, BlueTuskDataFormat.Binary);
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(binary));
        Assert.Equal(23U, BinaryPrimitives.ReadUInt32BigEndian(binary.AsSpan(4)));
    }

    [Fact]
    public void Anonymous_binary_record_uses_each_wire_field_oid()
    {
        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(2249),
                Schema = "pg_catalog",
                Name = "record",
                PostgreSqlKind = 'p',
                PostgreSqlCategory = 'P',
            },
        ]);
        var codec = GetCodec(registry, new BlueTuskTypeId(2249));
        var bytes = new byte[64];
        var writer = new BlueTuskWriter(bytes);
        writer.WriteInt32BigEndian(2);
        writer.WriteUInt32BigEndian(23);
        writer.WriteInt32BigEndian(4);
        writer.WriteInt32BigEndian(42);
        writer.WriteUInt32BigEndian(25);
        writer.WriteInt32BigEndian(4);
        writer.WriteUtf8("text");
        var reader = new BlueTuskReader(bytes.AsSpan(0, writer.WrittenCount));

        var value = codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, registry.Types.Single(
            type => type.Id == new BlueTuskTypeId(2249)));

        Assert.Equal(42, value[0].Value);
        Assert.Equal("int4", value[0].Type!.Name);
        Assert.Equal("text", value[1].Value);
        Assert.Equal("text", value[1].Type!.Name);
        Assert.All(value, field => Assert.Null(field.Name));
    }

    [Fact]
    public void Anonymous_text_record_preserves_raw_fields_when_oids_are_unavailable()
    {
        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(2249),
                Schema = "pg_catalog",
                Name = "record",
                PostgreSqlKind = 'p',
                PostgreSqlCategory = 'P',
            },
        ]);
        var codec = GetCodec(registry, new BlueTuskTypeId(2249));
        var reader = new BlueTuskReader("(42,\"a,b\",)"u8);

        var value = codec.ReadTyped(ref reader, BlueTuskDataFormat.Text, registry.Types.Single(
            type => type.Id == new BlueTuskTypeId(2249)));

        Assert.Equal("42", value[0].Value);
        Assert.Equal("a,b", value[1].Value);
        Assert.Null(value[2].Value);
        Assert.All(value, field => Assert.Null(field.Type));
    }

    [Theory]
    [InlineData("(1,2")]
    [InlineData("(\"unterminated)")]
    [InlineData("(1) trailing")]
    public void Malformed_record_text_is_rejected(string text)
    {
        var codec = CreateNamedCodec();

        Assert.Throws<InvalidOperationException>(
            () => ReadText(codec, text));
    }

    private static BlueTuskRecordCodec CreateNamedCodec()
    {
        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = AddressType.Id,
                Schema = AddressType.Schema,
                Name = AddressType.Name,
                PostgreSqlKind = 'c',
                PostgreSqlCategory = 'C',
                CompositeFields = AddressType.CompositeFields,
            },
        ]);
        return GetCodec(registry, AddressType.Id);
    }

    private static BlueTuskRecordCodec GetCodec(BlueTuskTypeRegistry registry, BlueTuskTypeId id)
    {
        Assert.True(registry.TryGetCodec(id, out var registered));
        return Assert.IsType<BlueTuskRecordCodec>(registered);
    }

    private static BlueTuskRecord ReadText(BlueTuskRecordCodec codec, string text)
    {
        var reader = new BlueTuskReader(Encoding.UTF8.GetBytes(text));
        return codec.ReadTyped(ref reader, BlueTuskDataFormat.Text, AddressType);
    }

    private static BlueTuskRecord RoundTrip(
        BlueTuskRecordCodec codec,
        BlueTuskRecord value,
        BlueTuskDataFormat format)
    {
        var bytes = Write(codec, value, format);
        var reader = new BlueTuskReader(bytes);
        return codec.ReadTyped(ref reader, format, AddressType);
    }

    private static byte[] Write(
        BlueTuskRecordCodec codec,
        BlueTuskRecord value,
        BlueTuskDataFormat format)
    {
        var destination = new byte[4096];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, format, AddressType);
        return destination.AsSpan(0, writer.WrittenCount).ToArray();
    }

    private static void AssertRecordEqual(BlueTuskRecord expected, BlueTuskRecord actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(expected[index].Type!.Id, actual[index].Type!.Id);
            Assert.Equal(expected[index].Value, actual[index].Value);
        }
    }
}

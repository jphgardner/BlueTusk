using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskCatalogueTextCodecTests
{
    [Fact]
    public void Internal_char_round_trips_every_byte_in_text_and_binary()
    {
        var codec = new BlueTuskInternalCharCodec();
        for (var value = 0; value <= byte.MaxValue; value++)
        {
            var expected = new BlueTuskInternalChar((byte)value);
            Assert.Equal(
                expected,
                RoundTrip(
                    codec,
                    BlueTuskBuiltInTypes.Char,
                    expected,
                    BlueTuskDataFormat.Binary));
            Assert.Equal(
                expected,
                RoundTrip(
                    codec,
                    BlueTuskBuiltInTypes.Char,
                    expected,
                    BlueTuskDataFormat.Text));
        }

        Assert.Empty(
            Write(
                codec,
                BlueTuskBuiltInTypes.Char,
                new BlueTuskInternalChar(0),
                BlueTuskDataFormat.Text));
        Assert.Equal(
            "A",
            Encoding.ASCII.GetString(
                Write(
                    codec,
                    BlueTuskBuiltInTypes.Char,
                    new BlueTuskInternalChar((byte)'A'),
                    BlueTuskDataFormat.Text)));
        Assert.Equal(
            "\\377",
            Encoding.ASCII.GetString(
                Write(
                    codec,
                    BlueTuskBuiltInTypes.Char,
                    new BlueTuskInternalChar(byte.MaxValue),
                    BlueTuskDataFormat.Text)));
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("\\888")]
    [InlineData("é")]
    public void Noncanonical_internal_char_text_is_rejected(string text)
    {
        Assert.Throws<InvalidOperationException>(
            () => Read(
                new BlueTuskInternalCharCodec(),
                BlueTuskBuiltInTypes.Char,
                Encoding.UTF8.GetBytes(text),
                BlueTuskDataFormat.Text));
    }

    [Fact]
    public void Refcursor_preserves_empty_and_utf8_portal_names()
    {
        var codec = new BlueTuskRefCursorCodec();
        var value = new BlueTuskRefCursor("portal 🐘");

        Assert.Equal(
            value,
            RoundTrip(codec, BlueTuskBuiltInTypes.RefCursor, value, BlueTuskDataFormat.Text));
        Assert.Equal(
            value,
            RoundTrip(codec, BlueTuskBuiltInTypes.RefCursor, value, BlueTuskDataFormat.Binary));
        Assert.Equal(
            new BlueTuskRefCursor(string.Empty),
            RoundTrip(
                codec,
                BlueTuskBuiltInTypes.RefCursor,
                new BlueTuskRefCursor(string.Empty),
                BlueTuskDataFormat.Binary));
    }

    [Fact]
    public void Jsonpath_validates_binary_version_and_preserves_server_text()
    {
        var codec = new BlueTuskJsonPathCodec();
        var value = new BlueTuskJsonPath("$.\"answer\"");

        Assert.Equal(
            value,
            RoundTrip(codec, BlueTuskBuiltInTypes.JsonPath, value, BlueTuskDataFormat.Binary));
        Assert.Equal(
            "01242E22616E7377657222",
            Convert.ToHexString(
                Write(
                    codec,
                    BlueTuskBuiltInTypes.JsonPath,
                    value,
                    BlueTuskDataFormat.Binary)));
        Assert.Throws<InvalidOperationException>(
            () => Read(
                codec,
                BlueTuskBuiltInTypes.JsonPath,
                [0, (byte)'$'],
                BlueTuskDataFormat.Binary));
    }

    [Fact]
    public void Node_tree_decodes_opaque_text_but_rejects_input()
    {
        var text =
            "{CONST :consttype 23 :consttypmod -1 :constvalue 4 [ 42 0 0 0 0 0 0 0 ]}";
        var bytes = Encoding.UTF8.GetBytes(text);
        var codec = new BlueTuskNodeTreeCodec();

        Assert.Equal(
            new BlueTuskNodeTree(text),
            Read(
                codec,
                BlueTuskBuiltInTypes.NodeTree,
                bytes,
                BlueTuskDataFormat.Text));
        Assert.Equal(
            new BlueTuskNodeTree(text),
            Read(
                codec,
                BlueTuskBuiltInTypes.NodeTree,
                bytes,
                BlueTuskDataFormat.Binary));
        Assert.Throws<NotSupportedException>(
            () => Write(
                codec,
                BlueTuskBuiltInTypes.NodeTree,
                new BlueTuskNodeTree(text),
                BlueTuskDataFormat.Binary));
    }

    [Fact]
    public void Aclitem_round_trips_text_and_always_selects_text_for_writes()
    {
        var codec = new BlueTuskAccessControlItemCodec();
        var value = new BlueTuskAccessControlItem("postgres=arwdDxt/postgres");

        Assert.Equal(
            value,
            RoundTrip(codec, BlueTuskBuiltInTypes.AclItem, value, BlueTuskDataFormat.Text));
        Assert.Equal(
            BlueTuskDataFormat.Text,
            codec.GetPreferredWriteFormat(value, BlueTuskBuiltInTypes.AclItem));
        Assert.Equal(BlueTuskDataFormat.Text, codec.DefaultWriteFormat);
        Assert.Throws<NotSupportedException>(
            () => Write(codec, BlueTuskBuiltInTypes.AclItem, value, BlueTuskDataFormat.Binary));
    }

    [Fact]
    public void Gtsvector_decodes_text_but_rejects_input()
    {
        var codec = new BlueTuskGistTextSearchVectorCodec();
        var value = new BlueTuskGistTextSearchVector("deadbeef");

        Assert.Equal(
            value,
            Read(
                codec,
                BlueTuskBuiltInTypes.GistTextSearchVector,
                Encoding.UTF8.GetBytes(value.Value),
                BlueTuskDataFormat.Text));
        Assert.Throws<NotSupportedException>(
            () => Write(
                codec,
                BlueTuskBuiltInTypes.GistTextSearchVector,
                value,
                BlueTuskDataFormat.Text));
    }

    [Fact]
    public void Catalogue_composes_internal_char_cursor_and_jsonpath_arrays()
    {
        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            Base(BlueTuskBuiltInTypes.Char, 'Z'),
            Array(1002, "_char", BlueTuskBuiltInTypes.Char.Id),
            Base(BlueTuskBuiltInTypes.RefCursor, 'U'),
            Array(2201, "_refcursor", BlueTuskBuiltInTypes.RefCursor.Id),
            Base(BlueTuskBuiltInTypes.JsonPath, 'U'),
            Array(4073, "_jsonpath", BlueTuskBuiltInTypes.JsonPath.Id),
        ]);

        AssertCodecType(registry, BlueTuskBuiltInTypes.Char.Id, typeof(BlueTuskInternalChar));
        AssertCodecType(registry, new BlueTuskTypeId(1002), typeof(BlueTuskInternalChar[]));
        AssertCodecType(registry, BlueTuskBuiltInTypes.RefCursor.Id, typeof(BlueTuskRefCursor));
        AssertCodecType(registry, new BlueTuskTypeId(2201), typeof(BlueTuskRefCursor[]));
        AssertCodecType(registry, BlueTuskBuiltInTypes.JsonPath.Id, typeof(BlueTuskJsonPath));
        AssertCodecType(registry, new BlueTuskTypeId(4073), typeof(BlueTuskJsonPath[]));
    }

    [Fact]
    public void Catalogue_composes_text_only_catalogue_arrays()
    {
        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            Base(BlueTuskBuiltInTypes.AclItem, 'U'),
            Array(1034, "_aclitem", BlueTuskBuiltInTypes.AclItem.Id),
            Base(BlueTuskBuiltInTypes.GistTextSearchVector, 'U'),
            Array(3644, "_gtsvector", BlueTuskBuiltInTypes.GistTextSearchVector.Id),
        ]);

        AssertCodecType(registry, BlueTuskBuiltInTypes.AclItem.Id, typeof(BlueTuskAccessControlItem));
        AssertCodecType(registry, new BlueTuskTypeId(1034), typeof(BlueTuskAccessControlItem[]));
        AssertCodecType(
            registry,
            BlueTuskBuiltInTypes.GistTextSearchVector.Id,
            typeof(BlueTuskGistTextSearchVector));
        AssertCodecType(registry, new BlueTuskTypeId(3644), typeof(BlueTuskGistTextSearchVector[]));

        Assert.True(registry.TryGetCodec(new BlueTuskTypeId(1034), out var aclArrayCodec));
        Assert.Equal(
            BlueTuskDataFormat.Text,
            Assert.IsAssignableFrom<IBlueTuskWriteFormatSelector>(aclArrayCodec)
                .GetPreferredWriteFormat(
                    System.Array.Empty<BlueTuskAccessControlItem>(),
                    BlueTuskBuiltInTypes.AclItem));
    }

    private static BlueTuskCatalogueType Base(
        BlueTuskTypeDescriptor type,
        char category) => new()
        {
            Id = type.Id,
            Schema = type.Schema,
            Name = type.Name,
            PostgreSqlKind = 'b',
            PostgreSqlCategory = category,
            ArrayType = type.ArrayType,
        };

    private static BlueTuskCatalogueType Array(
        uint oid,
        string name,
        BlueTuskTypeId elementType) => new()
        {
            Id = new BlueTuskTypeId(oid),
            Schema = "pg_catalog",
            Name = name,
            PostgreSqlKind = 'b',
            PostgreSqlCategory = 'A',
            ElementType = elementType,
        };

    private static void AssertCodecType(
        BlueTuskTypeRegistry registry,
        BlueTuskTypeId type,
        Type clrType)
    {
        Assert.True(registry.TryGetCodec(type, out var codec));
        Assert.Equal(clrType, codec!.ClrType);
    }

    private static T RoundTrip<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        BlueTuskDataFormat format) =>
        Read(codec, type, Write(codec, type, value, format), format);

    private static T Read<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        byte[] bytes,
        BlueTuskDataFormat format)
    {
        var reader = new BlueTuskReader(bytes);
        return codec.ReadTyped(ref reader, format, type);
    }

    private static byte[] Write<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        BlueTuskDataFormat format)
    {
        var destination = new byte[1024];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, format, type);
        return destination.AsSpan(0, writer.WrittenCount).ToArray();
    }
}

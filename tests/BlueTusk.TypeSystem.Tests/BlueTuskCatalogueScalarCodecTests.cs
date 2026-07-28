using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskCatalogueScalarCodecTests
{
    [Fact]
    public void Object_identifier_aliases_preserve_numeric_and_symbolic_forms()
    {
        AssertAlias(
            new BlueTuskObjectIdentifierCodec<BlueTuskRegClass>(),
            BlueTuskBuiltInTypes.RegClass,
            new BlueTuskRegClass(uint.MaxValue),
            new BlueTuskRegClass("public.orders"));
        AssertAlias(
            new BlueTuskObjectIdentifierCodec<BlueTuskRegProcedure>(),
            BlueTuskBuiltInTypes.RegProcedure,
            new BlueTuskRegProcedure(23),
            new BlueTuskRegProcedure("sum(integer)"));

        var numericText = Read(
            new BlueTuskObjectIdentifierCodec<BlueTuskRegType>(),
            BlueTuskBuiltInTypes.RegType,
            Encoding.UTF8.GetBytes("4294967295"),
            BlueTuskDataFormat.Text);
        Assert.True(numericText.Identifier.IsNumeric);
        Assert.Equal(uint.MaxValue, numericText.Identifier.Oid);
        Assert.Equal(
            "FFFFFFFF",
            Convert.ToHexString(
                Write(
                    new BlueTuskObjectIdentifierCodec<BlueTuskRegClass>(),
                    BlueTuskBuiltInTypes.RegClass,
                    new BlueTuskRegClass(uint.MaxValue),
                    BlueTuskDataFormat.Binary)));
    }

    [Fact]
    public void Built_in_registry_has_distinct_clr_types_for_every_object_identifier_alias()
    {
        var registry = BlueTuskBuiltInTypes.CreateRegistry();
        (BlueTuskTypeDescriptor Type, Type ClrType)[] aliases =
        [
            (BlueTuskBuiltInTypes.RegProc, typeof(BlueTuskRegProc)),
            (BlueTuskBuiltInTypes.RegProcedure, typeof(BlueTuskRegProcedure)),
            (BlueTuskBuiltInTypes.RegOper, typeof(BlueTuskRegOper)),
            (BlueTuskBuiltInTypes.RegOperator, typeof(BlueTuskRegOperator)),
            (BlueTuskBuiltInTypes.RegClass, typeof(BlueTuskRegClass)),
            (BlueTuskBuiltInTypes.RegType, typeof(BlueTuskRegType)),
            (BlueTuskBuiltInTypes.RegConfig, typeof(BlueTuskRegConfig)),
            (BlueTuskBuiltInTypes.RegDictionary, typeof(BlueTuskRegDictionary)),
            (BlueTuskBuiltInTypes.RegNamespace, typeof(BlueTuskRegNamespace)),
            (BlueTuskBuiltInTypes.RegRole, typeof(BlueTuskRegRole)),
            (BlueTuskBuiltInTypes.RegCollation, typeof(BlueTuskRegCollation)),
        ];

        foreach (var alias in aliases)
        {
            Assert.True(registry.TryGetCodec(alias.Type.Id, out var codec));
            Assert.Equal(alias.ClrType, codec!.ClrType);
            Assert.True(registry.TryGetType(alias.ClrType, out var inferredType, out _));
            Assert.Equal(alias.Type.Id, inferredType!.Id);
        }
    }

    [Fact]
    public void Catalogue_vectors_match_postgresql_binary_and_text_formats()
    {
        var int2Vector = new BlueTuskInt16Vector([1, 2, -3]);
        var oidVector = new BlueTuskObjectIdentifierVector([0, uint.MaxValue, 23]);
        const string int2Binary =
            "00000001" +
            "00000000" +
            "00000015" +
            "00000003" +
            "00000000" +
            "000000020001" +
            "000000020002" +
            "00000002FFFD";
        const string oidBinary =
            "00000001" +
            "00000000" +
            "0000001A" +
            "00000003" +
            "00000000" +
            "0000000400000000" +
            "00000004FFFFFFFF" +
            "0000000400000017";

        Assert.Equal(
            int2Vector,
            RoundTrip(
                new BlueTuskInt16VectorCodec(),
                BlueTuskBuiltInTypes.Int2Vector,
                int2Vector,
                BlueTuskDataFormat.Binary));
        Assert.Equal(
            oidVector,
            RoundTrip(
                new BlueTuskObjectIdentifierVectorCodec(),
                BlueTuskBuiltInTypes.OidVector,
                oidVector,
                BlueTuskDataFormat.Text));
        Assert.Equal(
            int2Binary,
            Convert.ToHexString(
                Write(
                    new BlueTuskInt16VectorCodec(),
                    BlueTuskBuiltInTypes.Int2Vector,
                    int2Vector,
                    BlueTuskDataFormat.Binary)));
        Assert.Equal(
            oidBinary,
            Convert.ToHexString(
                Write(
                    new BlueTuskObjectIdentifierVectorCodec(),
                    BlueTuskBuiltInTypes.OidVector,
                    oidVector,
                    BlueTuskDataFormat.Binary)));
        Assert.Equal(
            "1 2 -3",
            Encoding.UTF8.GetString(
                Write(
                    new BlueTuskInt16VectorCodec(),
                    BlueTuskBuiltInTypes.Int2Vector,
                    int2Vector,
                    BlueTuskDataFormat.Text)));
    }

    [Fact]
    public void Catalogue_vectors_are_immutable_and_keep_empty_vector_shape()
    {
        short[] source = [1, 2];
        var value = new BlueTuskInt16Vector(source);
        source[0] = 9;
        var codec = new BlueTuskInt16VectorCodec();
        var empty = new BlueTuskInt16Vector([]);

        Assert.Equal<short>([1, 2], value);
        Assert.Equal(
            BlueTuskDataFormat.Binary,
            codec.GetPreferredWriteFormat(value, BlueTuskBuiltInTypes.Int2Vector));
        Assert.Equal(
            BlueTuskDataFormat.Text,
            codec.GetPreferredWriteFormat(empty, BlueTuskBuiltInTypes.Int2Vector));
        Assert.Equal(
            "0000000100000000000000150000000000000000",
            Convert.ToHexString(
                Write(
                    codec,
                    BlueTuskBuiltInTypes.Int2Vector,
                    empty,
                    BlueTuskDataFormat.Binary)));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(4, 1)]
    [InlineData(8, 26)]
    [InlineData(16, 1)]
    public void Invalid_int2vector_binary_shape_is_rejected(int offset, int replacement)
    {
        var bytes = Write(
            new BlueTuskInt16VectorCodec(),
            BlueTuskBuiltInTypes.Int2Vector,
            new BlueTuskInt16Vector([1]),
            BlueTuskDataFormat.Binary);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            bytes.AsSpan(offset),
            replacement);

        Assert.Throws<InvalidOperationException>(
            () => Read(
                new BlueTuskInt16VectorCodec(),
                BlueTuskBuiltInTypes.Int2Vector,
                bytes,
                BlueTuskDataFormat.Binary));
    }

    [Fact]
    public void Array_like_base_types_remain_base_types_and_their_arrays_are_composed()
    {
        var vector = BlueTuskTypeCatalogue.CreateDescriptor(new BlueTuskCatalogueType
        {
            Id = BlueTuskBuiltInTypes.Int2Vector.Id,
            Schema = "pg_catalog",
            Name = "int2vector",
            PostgreSqlKind = 'b',
            PostgreSqlCategory = 'A',
            ElementType = BlueTuskBuiltInTypes.Int2.Id,
            ArrayType = new BlueTuskTypeId(1006),
        });
        var vectorArray = BlueTuskTypeCatalogue.CreateDescriptor(new BlueTuskCatalogueType
        {
            Id = new BlueTuskTypeId(1006),
            Schema = "pg_catalog",
            Name = "_int2vector",
            PostgreSqlKind = 'b',
            PostgreSqlCategory = 'A',
            ElementType = BlueTuskBuiltInTypes.Int2Vector.Id,
        });

        Assert.Equal(BlueTuskTypeKind.Base, vector.Kind);
        Assert.Equal(BlueTuskTypeKind.Array, vectorArray.Kind);

        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            ToCatalogueType(vector),
            ToCatalogueType(vectorArray),
        ]);
        Assert.True(registry.TryGetCodec(vectorArray.Id, out var codec));
        Assert.Equal(typeof(BlueTuskInt16Vector[]), codec!.ClrType);
    }

    private static BlueTuskCatalogueType ToCatalogueType(BlueTuskTypeDescriptor type) => new()
    {
        Id = type.Id,
        Schema = type.Schema,
        Name = type.Name,
        PostgreSqlKind = 'b',
        PostgreSqlCategory = 'A',
        ElementType = type.ElementType,
        ArrayType = type.ArrayType,
    };

    private static void AssertAlias<T>(
        BlueTuskObjectIdentifierCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T numeric,
        T symbolic)
        where T : struct, IBlueTuskObjectIdentifierValue<T>
    {
        Assert.Equal(numeric, RoundTrip(codec, type, numeric, BlueTuskDataFormat.Binary));
        Assert.Equal(symbolic, RoundTrip(codec, type, symbolic, BlueTuskDataFormat.Text));
        Assert.Equal(
            BlueTuskDataFormat.Binary,
            codec.GetPreferredWriteFormat(numeric, type));
        Assert.Equal(
            BlueTuskDataFormat.Text,
            codec.GetPreferredWriteFormat(symbolic, type));
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

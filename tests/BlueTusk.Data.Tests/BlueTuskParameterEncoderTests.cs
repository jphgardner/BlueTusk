using System.Buffers.Binary;
using System.Data;
using System.Text;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskParameterEncoderTests
{
    [Fact]
    public void Encodes_int32_as_binary_int4()
    {
        var encoded = BlueTuskParameterEncoder.Encode(new BlueTuskParameter<int>(42));

        Assert.Equal(23U, encoded.TypeOid);
        Assert.Equal(1, encoded.FormatCode);
        Assert.Equal(42, BinaryPrimitives.ReadInt32BigEndian(encoded.Value!.Value.Span));
    }

    [Fact]
    public void Encodes_null_with_an_explicit_db_type()
    {
        var encoded = BlueTuskParameterEncoder.Encode(
            new BlueTuskParameter(DBNull.Value) { DbType = DbType.String });

        Assert.Equal(25U, encoded.TypeOid);
        Assert.Equal(0, encoded.FormatCode);
        Assert.Null(encoded.Value);
    }

    [Fact]
    public void Encodes_custom_type_text_with_an_explicit_oid()
    {
        var encoded = BlueTuskParameterEncoder.Encode(
            new BlueTuskParameter("custom-value") { PostgreSqlTypeOid = 99_999 });

        Assert.Equal(99_999U, encoded.TypeOid);
        Assert.Equal(0, encoded.FormatCode);
        Assert.Equal("custom-value", Encoding.UTF8.GetString(encoded.Value!.Value.Span));
    }

    [Fact]
    public void Rejects_an_untyped_null()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => BlueTuskParameterEncoder.Encode(new BlueTuskParameter(null)));

        Assert.Contains("requires DbType or PostgreSqlTypeOid", exception.Message, StringComparison.Ordinal);
    }
}

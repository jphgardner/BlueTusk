using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskScalarCodecTests
{
    [Fact]
    public void Fixed_width_scalars_round_trip_in_text_and_binary_formats()
    {
        AssertRoundTrip(new BlueTuskBooleanCodec(), BlueTuskBuiltInTypes.Boolean, true);
        AssertRoundTrip(new BlueTuskInt16Codec(), BlueTuskBuiltInTypes.Int2, short.MinValue);
        AssertRoundTrip(new BlueTuskInt32Codec(), BlueTuskBuiltInTypes.Int4, int.MaxValue);
        AssertRoundTrip(new BlueTuskInt64Codec(), BlueTuskBuiltInTypes.Int8, long.MinValue);
        AssertRoundTrip(new BlueTuskUInt32Codec(), BlueTuskBuiltInTypes.Oid, uint.MaxValue);
        AssertRoundTrip(new BlueTuskSingleCodec(), BlueTuskBuiltInTypes.Float4, -123.5F);
        AssertRoundTrip(new BlueTuskDoubleCodec(), BlueTuskBuiltInTypes.Float8, double.PositiveInfinity);
    }

    [Fact]
    public void Utf8_text_and_jsonb_round_trip_in_both_formats()
    {
        const string text = "BlueTusk 🐘";
        const string json = "{\"answer\":42}";

        AssertRoundTrip(new BlueTuskStringCodec(), BlueTuskBuiltInTypes.Text, text);
        AssertRoundTrip(new BlueTuskJsonbCodec(), BlueTuskBuiltInTypes.Jsonb, json);

        Span<byte> destination = stackalloc byte[64];
        var writer = new BlueTuskWriter(destination);
        new BlueTuskJsonbCodec().WriteTyped(
            ref writer,
            json,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.Jsonb);
        Assert.Equal(1, destination[0]);
    }

    [Fact]
    public void Bytea_supports_binary_hex_and_legacy_escape_formats()
    {
        byte[] expected = [0, 1, 92, 127, 255];
        AssertRoundTrip(new BlueTuskByteArrayCodec(), BlueTuskBuiltInTypes.Bytea, expected);

        var escapedReader = new BlueTuskReader("A\\134\\000Z"u8);
        var escaped = new BlueTuskByteArrayCodec().ReadTyped(
            ref escapedReader,
            BlueTuskDataFormat.Text,
            BlueTuskBuiltInTypes.Bytea);

        Assert.Equal(new byte[] { (byte)'A', 92, 0, (byte)'Z' }, escaped);
    }

    [Fact]
    public void Uuid_binary_uses_postgresql_network_byte_order()
    {
        var value = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Span<byte> destination = stackalloc byte[16];
        var writer = new BlueTuskWriter(destination);
        var codec = new BlueTuskGuidCodec();

        codec.WriteTyped(
            ref writer,
            value,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.Uuid);
        var reader = new BlueTuskReader(destination);

        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), destination.ToArray());
        Assert.Equal(
            value,
            codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, BlueTuskBuiltInTypes.Uuid));
    }

    [Fact]
    public void Strict_utf8_rejects_invalid_input_and_unpaired_surrogates()
    {
        Assert.Throws<DecoderFallbackException>(ReadInvalidUtf8);
        Assert.Throws<EncoderFallbackException>(WriteInvalidUtf16);
    }

    private static void AssertRoundTrip<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value)
    {
        Span<byte> destination = stackalloc byte[512];
        foreach (var format in new[] { BlueTuskDataFormat.Text, BlueTuskDataFormat.Binary })
        {
            var writer = new BlueTuskWriter(destination);
            codec.WriteTyped(ref writer, value, format, type);
            var reader = new BlueTuskReader(destination[..writer.WrittenCount]);
            var actual = codec.ReadTyped(ref reader, format, type);

            if (value is byte[] expectedBytes)
            {
                Assert.Equal(expectedBytes, Assert.IsType<byte[]>(actual));
            }
            else
            {
                Assert.Equal(value, actual);
            }

            Assert.Equal(0, reader.Remaining);
        }
    }

    private static void ReadInvalidUtf8()
    {
        var reader = new BlueTuskReader(new byte[] { 0xC3, 0x28 });
        _ = reader.ReadRemainingUtf8();
    }

    private static void WriteInvalidUtf16()
    {
        Span<byte> destination = stackalloc byte[16];
        var writer = new BlueTuskWriter(destination);
        writer.WriteUtf8("\ud800");
    }
}

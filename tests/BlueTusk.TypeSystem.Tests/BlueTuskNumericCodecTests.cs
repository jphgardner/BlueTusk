using System.Globalization;
using System.Numerics;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskNumericCodecTests
{
    private readonly BlueTuskNumericCodec _codec = new();

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0.000001", "-0.000001")]
    [InlineData("123456789012345678901234567890.12345678901234567890", "123456789012345678901234567890.12345678901234567890")]
    [InlineData("1.2300e3", "1230.0")]
    [InlineData("1e-8", "0.00000001")]
    [InlineData("NaN", "NaN")]
    [InlineData("Infinity", "Infinity")]
    [InlineData("-Infinity", "-Infinity")]
    public void Parses_and_formats_arbitrary_precision_text(string text, string expected)
    {
        var value = BlueTuskNumeric.Parse(text);

        Assert.Equal(expected, value.ToString());
        AssertRoundTrip(value, BlueTuskDataFormat.Text);
        AssertRoundTrip(value, BlueTuskDataFormat.Binary);
    }

    [Fact]
    public void Binary_numeric_uses_postgresql_base_10000_groups()
    {
        var value = BlueTuskNumeric.Parse("12345.6789");
        Span<byte> destination = stackalloc byte[32];
        var writer = new BlueTuskWriter(destination);

        _codec.WriteTyped(
            ref writer,
            value,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.Numeric);

        Assert.Equal("0003000100000004000109291A85", Convert.ToHexString(destination[..writer.WrittenCount]));
    }

    [Fact]
    public void Decimal_conversion_is_explicit_and_checked()
    {
        var value = (BlueTuskNumeric)12345.6789m;

        Assert.Equal(12345.6789m, value.ToDecimal());
        Assert.Throws<OverflowException>(
            () => BlueTuskNumeric.Parse("1234567890123456789012345678901234567890").ToDecimal());
        Assert.Throws<InvalidCastException>(() => BlueTuskNumeric.NaN.ToDecimal());
    }

    [Fact]
    public void Binary_decoder_rejects_invalid_base_10000_digits()
    {
        byte[] invalid = [0, 1, 0, 0, 0, 0, 0, 0, 0x27, 0x10];

        Assert.Throws<InvalidOperationException>(() => ReadBinary(invalid));
    }

    [Fact]
    public void Constructor_preserves_scale_and_unscaled_value()
    {
        var value = new BlueTuskNumeric(BigInteger.Parse("12300", CultureInfo.InvariantCulture), 4);

        Assert.Equal("1.2300", value.ToString());
        Assert.Equal(4, value.Scale);
    }

    private void AssertRoundTrip(BlueTuskNumeric expected, BlueTuskDataFormat format)
    {
        Span<byte> destination = stackalloc byte[512];
        var writer = new BlueTuskWriter(destination);
        _codec.WriteTyped(ref writer, expected, format, BlueTuskBuiltInTypes.Numeric);
        var reader = new BlueTuskReader(destination[..writer.WrittenCount]);

        var actual = _codec.ReadTyped(ref reader, format, BlueTuskBuiltInTypes.Numeric);

        Assert.Equal(expected, actual);
        Assert.Equal(0, reader.Remaining);
    }

    private void ReadBinary(byte[] value)
    {
        var reader = new BlueTuskReader(value);
        _ = _codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, BlueTuskBuiltInTypes.Numeric);
    }
}

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskMoneyCodecTests
{
    [Fact]
    public void Money_binary_is_a_scale_aware_signed_int64()
    {
        var codec = new BlueTuskMoneyCodec(new BlueTuskMoneyFormat("en_US.utf8", 2));
        var value = new BlueTuskMoney(123_456, 2);
        var bytes = Write(codec, value, BlueTuskDataFormat.Binary);

        Assert.Equal("000000000001E240", Convert.ToHexString(bytes));
        Assert.Equal("1234.56", value.ToString());
        AssertRoundTrip(codec, value);
    }

    [Fact]
    public void Money_text_parses_locale_currency_grouping_and_negative_patterns()
    {
        var english = new BlueTuskMoneyCodec(new BlueTuskMoneyFormat("en_US.UTF-8", 2));
        var german = new BlueTuskMoneyCodec(new BlueTuskMoneyFormat("de_DE.UTF-8", 2));

        Assert.Equal(new BlueTuskMoney(123_456, 2), ReadText(english, "$1,234.56"));
        Assert.Equal(new BlueTuskMoney(-123_456, 2), ReadText(german, "-1.234,56 €"));
        Assert.Equal("-1234,56", System.Text.Encoding.UTF8.GetString(
            Write(german, new BlueTuskMoney(-123_456, 2), BlueTuskDataFormat.Text)));
    }

    [Fact]
    public void Money_supports_the_complete_signed_int64_wire_range()
    {
        var codec = new BlueTuskMoneyCodec(new BlueTuskMoneyFormat("C", 2));

        Assert.Equal(
            new BlueTuskMoney(long.MinValue, 2),
            ReadText(codec, "-$92,233,720,368,547,758.08"));
        AssertRoundTrip(codec, new BlueTuskMoney(long.MinValue, 2));
        AssertRoundTrip(codec, new BlueTuskMoney(long.MaxValue, 2));
    }

    [Fact]
    public void Money_codec_rejects_a_value_with_a_different_locale_scale()
    {
        var codec = new BlueTuskMoneyCodec(new BlueTuskMoneyFormat("en-US", 2));

        Assert.Throws<InvalidOperationException>(() =>
            Write(codec, new BlueTuskMoney(1234, 0), BlueTuskDataFormat.Binary));
    }

    private static void AssertRoundTrip(BlueTuskMoneyCodec codec, BlueTuskMoney value)
    {
        foreach (var format in new[] { BlueTuskDataFormat.Text, BlueTuskDataFormat.Binary })
        {
            var bytes = Write(codec, value, format);
            var reader = new BlueTuskReader(bytes);
            Assert.Equal(
                value,
                codec.ReadTyped(ref reader, format, BlueTuskBuiltInTypes.Money));
            Assert.Equal(0, reader.Remaining);
        }
    }

    private static BlueTuskMoney ReadText(BlueTuskMoneyCodec codec, string value)
    {
        var reader = new BlueTuskReader(System.Text.Encoding.UTF8.GetBytes(value));
        return codec.ReadTyped(ref reader, BlueTuskDataFormat.Text, BlueTuskBuiltInTypes.Money);
    }

    private static byte[] Write(
        BlueTuskMoneyCodec codec,
        BlueTuskMoney value,
        BlueTuskDataFormat format)
    {
        Span<byte> destination = stackalloc byte[128];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, format, BlueTuskBuiltInTypes.Money);
        return destination[..writer.WrittenCount].ToArray();
    }
}

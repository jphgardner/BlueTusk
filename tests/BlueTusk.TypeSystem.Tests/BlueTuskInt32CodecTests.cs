using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskInt32CodecTests
{
    private readonly BlueTuskInt32Codec _codec = new();

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(-2_147_483_648)]
    [InlineData(2_147_483_647)]
    public void Round_trips_binary_values(int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        var writer = new BlueTuskWriter(buffer);
        _codec.WriteTyped(ref writer, value, BlueTuskDataFormat.Binary, BlueTuskBuiltInTypes.Int4);
        var reader = new BlueTuskReader(buffer);

        var result = _codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, BlueTuskBuiltInTypes.Int4);

        Assert.Equal(value, result);
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("-17", -17)]
    public void Reads_text_values_using_invariant_format(string text, int expected)
    {
        var reader = new BlueTuskReader(Encoding.UTF8.GetBytes(text));

        Assert.Equal(expected, _codec.ReadTyped(ref reader, BlueTuskDataFormat.Text, BlueTuskBuiltInTypes.Int4));
    }

    [Fact]
    public void Rejects_invalid_binary_length()
    {
        Assert.Throws<InvalidOperationException>(() => ReadInvalidBinaryValue(_codec));
    }

    private static void ReadInvalidBinaryValue(BlueTuskInt32Codec codec)
    {
        var reader = new BlueTuskReader([0, 1, 2]);
        codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, BlueTuskBuiltInTypes.Int4);
    }
}

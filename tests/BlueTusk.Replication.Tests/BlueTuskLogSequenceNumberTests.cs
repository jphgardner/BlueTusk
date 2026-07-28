using BlueTusk.TypeSystem;

namespace BlueTusk.Replication.Tests;

public sealed class BlueTuskLogSequenceNumberTests
{
    [Theory]
    [InlineData("0/0", 0UL)]
    [InlineData("16/B374D848", 0x00000016B374D848UL)]
    [InlineData("FFFFFFFF/FFFFFFFF", ulong.MaxValue)]
    public void Parses_and_formats_postgresql_positions(string text, ulong value)
    {
        var position = BlueTuskLogSequenceNumber.Parse(text);

        Assert.Equal(value, position.Value);
        Assert.True(BlueTuskLogSequenceNumber.TryParse(text, out var parsed));
        Assert.Equal(position, parsed);
        Assert.Equal(text, position.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("/1")]
    [InlineData("1/")]
    [InlineData("not/an-lsn")]
    public void Rejects_invalid_positions(string text)
    {
        Assert.False(BlueTuskLogSequenceNumber.TryParse(text, out _));
        Assert.ThrowsAny<Exception>(() => BlueTuskLogSequenceNumber.Parse(text));
    }

    [Fact]
    public void Supports_ordering_and_checked_position_advances()
    {
        var start = new BlueTuskLogSequenceNumber(41);
        var end = start + 1;

        Assert.True(start < end);
        Assert.True(end > start);
        Assert.Equal(42UL, end.Value);
        Assert.Throws<OverflowException>(
            () => _ = new BlueTuskLogSequenceNumber(ulong.MaxValue) + 1);
    }
}

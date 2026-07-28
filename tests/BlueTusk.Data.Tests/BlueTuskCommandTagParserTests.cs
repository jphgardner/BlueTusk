namespace BlueTusk.Data.Tests;

public sealed class BlueTuskCommandTagParserTests
{
    [Theory]
    [InlineData("COPY 0", 0L)]
    [InlineData("COPY 42", 42L)]
    [InlineData("COPY 2147483648", 2_147_483_648L)]
    public void Parses_copy_row_counts_as_int64(string commandTag, long expected)
    {
        Assert.True(BlueTuskCommandTagParser.TryGetRowsAffected(commandTag, out var count));
        Assert.Equal(expected, count);
    }

    [Fact]
    public void AdoNet_count_rejects_values_above_int32()
    {
        Assert.False(
            BlueTuskCommandTagParser.TryGetRecordsAffected(
                "COPY 2147483648",
                out _));
    }
}

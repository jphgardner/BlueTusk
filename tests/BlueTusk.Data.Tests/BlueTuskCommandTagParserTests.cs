namespace BlueTusk.Data.Tests;

public sealed class BlueTuskCommandTagParserTests
{
    [Theory]
    [InlineData("INSERT 0 2", 2)]
    [InlineData("UPDATE 0", 0)]
    [InlineData("DELETE 42", 42)]
    [InlineData("MERGE 12", 12)]
    [InlineData("  INSERT   0   9  ", 9)]
    [InlineData("MOVE 3", 3)]
    [InlineData("FETCH 7", 7)]
    public void Preserves_counted_command_and_space_handling(string tag, int expected)
    {
        Assert.True(BlueTuskCommandTagParser.TryGetRecordsAffected(tag, out var count));
        Assert.Equal(expected, count);
    }

    [Fact]
    public void Count_parsing_does_not_allocate_per_command()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            _ = BlueTuskCommandTagParser.TryGetRecordsAffected("INSERT 0 42", out _);
        }
        var start = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0;
        for (var iteration = 0; iteration < 10000; iteration++)
        {
            if (BlueTuskCommandTagParser.TryGetRecordsAffected("INSERT 0 42", out var count)) { sum += count; }
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(420000, sum);
        Assert.Equal(0, allocated);
    }

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

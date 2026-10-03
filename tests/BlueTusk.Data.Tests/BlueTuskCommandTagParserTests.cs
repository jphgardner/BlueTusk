using System.Runtime.CompilerServices;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskCommandTagParserTests
{
    private static object? _allocationControl;

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
        var (sum, allocated) = MeasureAllocations(static () =>
            BlueTuskCommandTagParser.TryGetRecordsAffected("INSERT 0 42", out var count) ? count : 0);
        Assert.Equal(420000, sum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Allocation_measurement_detects_an_allocating_operation()
    {
        try
        {
            var (sum, allocated) = MeasureAllocations(static () =>
            {
                Volatile.Write(ref _allocationControl, new object());
                return 42;
            });
            Assert.Equal(420000, sum);
            Assert.True(allocated >= 10000 * IntPtr.Size);
        }
        finally
        {
            Volatile.Write(ref _allocationControl, null);
        }
    }

    // Compile the measurement boundary before entering it, and keep assertions in the caller.
    // Every operation allocation still counts; no samples are discarded or budget subtracted.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static (int Sum, long Allocated) MeasureAllocations(Func<int> operation)
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            _ = operation();
        }
        var start = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0;
        for (var iteration = 0; iteration < 10000; iteration++)
        {
            sum += operation();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        return (sum, allocated);
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

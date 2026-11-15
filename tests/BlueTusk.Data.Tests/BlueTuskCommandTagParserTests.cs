using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskCommandTagParserTests
{
    private const int Commands = 10_000;
    private const int WarmupCommands = 1_000;
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
        var measurement = MeasureOnDedicatedThread(ParseCount, Commands);
        Assert.Equal(Commands, measurement.Commands);
        Assert.Equal(42L * Commands, measurement.Sum);
        Assert.Equal(0, measurement.AllocatedBytes);
    }

    [Fact]
    public void Allocation_measurement_detects_an_allocating_operation()
    {
        try
        {
            var measurement = MeasureOnDedicatedThread(AllocateObject, Commands);
            Assert.Equal(42L * Commands, measurement.Sum);
            // Each command allocates one object, and the smallest object is three pointers.
            Assert.True(
                measurement.AllocatedBytes >= Commands * 3L * IntPtr.Size,
                $"Expected at least {Commands * 3L * IntPtr.Size} bytes for {Commands} allocations; measured {measurement.AllocatedBytes}.");
        }
        finally
        {
            Volatile.Write(ref _allocationControl, null);
        }
    }

    [Fact]
    public void Allocation_measurement_retires_setup_allocation_context_before_counting()
    {
        long precedingFullCollection = 0;
        try
        {
            var measurement = MeasureOnDedicatedThread(
                ParseCount,
                Commands,
                prepare: () =>
                {
                    // A runtime can service this request with either a concurrent or a blocking GC.
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
                    Volatile.Write(ref _allocationControl, new byte[64]);
                    precedingFullCollection = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
                });

            Assert.True(
                GC.GetGCMemoryInfo(GCKind.FullBlocking).Index > precedingFullCollection,
                "The measurement must complete a blocking collection after the setup allocation.");
            Assert.Equal(Commands, measurement.Commands);
            Assert.Equal(42L * Commands, measurement.Sum);
            Assert.Equal(0, measurement.AllocatedBytes);
        }
        finally
        {
            Volatile.Write(ref _allocationControl, null);
        }
    }

    private static int ParseCount() =>
        BlueTuskCommandTagParser.TryGetRecordsAffected("INSERT 0 42", out var count) ? count : 0;

    private static int AllocateObject()
    {
        Volatile.Write(ref _allocationControl, new object());
        return 42;
    }

    // A dedicated thread runs nothing but the measurement: no test framework work, continuations or
    // synchronization context can execute on it while the window is open.
    private static Measurement MeasureOnDedicatedThread(
        Func<int> operation,
        int commands,
        Action? prepare = null)
    {
        Measurement measurement = default;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // A first pass compiles the measurement and the operation, so preparation that must
                // immediately precede the measured window is not separated from it by JIT work.
                _ = Measure(operation, commands);
                prepare?.Invoke();
                measurement = Measure(operation, commands);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Allocation measurement",
        };
        thread.Start();
        thread.Join();
        failure?.Throw();
        return measurement;
    }

    // Every operation allocation in the window counts; nothing is subtracted, sampled or retried.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static Measurement Measure(Func<int> operation, int commands)
    {
        for (var iteration = 0; iteration < WarmupCommands; iteration++)
        {
            _ = operation();
        }

        // A blocking collection completes any in-progress background GC and retires the
        // measuring thread's allocation context. The background GC mark phase can otherwise
        // charge an unused allocation-context tail to this thread (dotnet/runtime#134724).
        GC.Collect();
        var start = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0L;
        var executed = 0;
        do
        {
            sum += operation();
            executed++;
        }
        while (executed < commands);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        return new Measurement(sum, executed, allocated);
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

    private readonly record struct Measurement(long Sum, int Commands, long AllocatedBytes);
}

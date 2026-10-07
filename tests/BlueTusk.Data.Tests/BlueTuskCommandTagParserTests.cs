using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskCommandTagParserTests
{
    private const int Commands = 10_000;
    private const int WarmupCommands = 1_000;
    private static object? _allocationControl;
    private static byte[][]? _backgroundGcBallast;

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
        var measurement = MeasureOnDedicatedThread(ParseCount, Commands, prepare: null, gate: null);
        Assert.Equal(Commands, measurement.Commands);
        Assert.Equal(42L * Commands, measurement.Sum);
        Assert.Equal(0, measurement.AllocatedBytes);
    }

    [Fact]
    public void Allocation_measurement_detects_an_allocating_operation()
    {
        try
        {
            var measurement = MeasureOnDedicatedThread(AllocateObject, Commands, prepare: null, gate: null);
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

    // CI failures (1952, 2240 and 3008 bytes) were not parser allocations. When a background GC ends its
    // mark phase it voids every thread's allocation context without crediting the unused tail back to that
    // thread's allocated-bytes counter (dotnet/runtime#134724), so a thread that allocated after the
    // background GC started is charged up to one allocation quantum it never used. This reproduces that
    // sequence: a background GC starts, the measuring thread allocates, and the window is held open until
    // the background GC has completed.
    [Fact]
    public void Allocation_measurement_is_not_charged_by_a_background_gc_in_progress()
    {
        Assert.SkipWhen(
            GCSettings.LatencyMode == GCLatencyMode.Batch,
            "Background (concurrent) garbage collection is disabled in this process.");
        var gate = new WindowGate();
        var releaser = new Thread(() => HoldWindowUntilBackgroundGcCompletes(gate))
        {
            IsBackground = true,
            Name = "Allocation window releaser",
        };
        releaser.Start();
        try
        {
            var measurement = MeasureOnDedicatedThread(
                ParseCount,
                Commands,
                prepare: () =>
                {
                    gate.StartedBackgroundGcAfter(StartBackgroundGc());
                    Volatile.Write(ref _allocationControl, new byte[64]);
                },
                gate);
            Assert.True(
                GC.GetGCMemoryInfo(GCKind.Background).Index > gate.BackgroundGcBefore,
                "The background GC started before the window must have completed by the end of the test.");
            Assert.Equal(42L * measurement.Commands, measurement.Sum);
            Assert.Equal(0, measurement.AllocatedBytes);
        }
        finally
        {
            gate.Release();
            releaser.Join();
            Volatile.Write(ref _allocationControl, null);
            _backgroundGcBallast = null;
        }
    }

    private static int ParseCount() =>
        BlueTuskCommandTagParser.TryGetRecordsAffected("INSERT 0 42", out var count) ? count : 0;

    private static int AllocateObject()
    {
        Volatile.Write(ref _allocationControl, new object());
        return 42;
    }

    // Live small objects make the GC grant a background collection instead of a blocking one for a
    // non-blocking request; the request is repeated until a background GC is observed in progress.
    private static long StartBackgroundGc()
    {
        var ballast = new byte[16_384][];
        for (var index = 0; index < ballast.Length; index++)
        {
            ballast[index] = new byte[1_000];
        }
        _backgroundGcBallast = ballast;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var background = GC.GetGCMemoryInfo(GCKind.Background).Index;
            var blocking = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
            var gen2 = GC.CollectionCount(2);
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
            if (GC.CollectionCount(2) > gen2 &&
                GC.GetGCMemoryInfo(GCKind.FullBlocking).Index == blocking &&
                GC.GetGCMemoryInfo(GCKind.Background).Index == background)
            {
                return background;
            }
        }
        throw new InvalidOperationException("The runtime did not start a background GC.");
    }

    private static void HoldWindowUntilBackgroundGcCompletes(WindowGate gate)
    {
        var deadline = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
        while (!gate.IsReleased &&
               (gate.BackgroundGcBefore < 0 || GC.GetGCMemoryInfo(GCKind.Background).Index <= gate.BackgroundGcBefore) &&
               Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(1);
        }
        gate.Release();
    }

    // A dedicated thread runs nothing but the measurement: no test framework work, continuations or
    // synchronization context can execute on it while the window is open.
    private static Measurement MeasureOnDedicatedThread(
        Func<int> operation,
        int commands,
        Action? prepare,
        WindowGate? gate)
    {
        Measurement measurement = default;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // A first pass compiles the measurement and the operation, so preparation that must
                // immediately precede the measured window is not separated from it by JIT work.
                _ = Measure(operation, commands, gate: null);
                prepare?.Invoke();
                measurement = Measure(operation, commands, gate);
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
    private static Measurement Measure(Func<int> operation, int commands, WindowGate? gate)
    {
        for (var iteration = 0; iteration < WarmupCommands; iteration++)
        {
            _ = operation();
        }

        // A blocking collection first waits for any background GC in progress, then retires every
        // allocation context and credits each unused tail back. This thread therefore enters the window
        // owning no allocation context: only an allocation inside the window can acquire one, and a
        // background GC ending inside the window has nothing of this thread's to void.
        GC.Collect();
        var start = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0L;
        var executed = 0;
        do
        {
            sum += operation();
            executed++;
        }
        while (executed < commands || (gate is not null && !gate.IsReleased));
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

    private sealed class WindowGate
    {
        private volatile bool _released;
        private long _backgroundGcBefore = -1;

        public bool IsReleased => _released;

        public long BackgroundGcBefore => Volatile.Read(ref _backgroundGcBefore);

        public void StartedBackgroundGcAfter(long backgroundGcIndex) => Volatile.Write(ref _backgroundGcBefore, backgroundGcIndex);

        public void Release() => _released = true;
    }
}

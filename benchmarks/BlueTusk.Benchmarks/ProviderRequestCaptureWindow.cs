using System.Diagnostics;

namespace BlueTusk.Benchmarks;

internal static partial class ProviderRequestCapture
{
    internal sealed record WorkerSamples(long[] RequestTicks, int Count);

    internal sealed record Window(
        long ElapsedTicks,
        long CompletedOperations,
        long AllocatedBytes,
        double CpuMilliseconds,
        long PeakWorkingSetBytes,
        int[] GcCollections,
        WorkerSamples[] Workers);

    internal static async Task<Window> MeasureWindowAsync(
        IReadOnlyList<Func<CancellationToken, Task>> operations,
        double seconds,
        int capacityPerWorker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count is < 1 or > 256 || !double.IsFinite(seconds) || seconds <= 0 ||
            capacityPerWorker < 0 || (long)capacityPerWorker * operations.Count > 32_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(operations), "The measurement must have bounded workers, samples, and a positive finite window.");
        }
        using var failureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = failureCancellation.Token;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = operations.Select(_ => new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var buffers = operations.Select(_ => new long[capacityPerWorker]).ToArray();
        var counts = new long[operations.Count];
        long deadline = 0;
        var tasks = operations.Select((operation, index) => Task.Run(async () =>
        {
            ready[index].SetResult();
            try
            {
                await start.Task.WaitAsync(token);
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    token.ThrowIfCancellationRequested();
                    if (capacityPerWorker != 0 && counts[index] == capacityPerWorker)
                    {
                        throw new InvalidOperationException("Raw sample capacity exhausted; increase the explicit bound. No truncated capture is accepted.");
                    }
                    var began = Stopwatch.GetTimestamp();
                    await operation(token);
                    var elapsed = Stopwatch.GetTimestamp() - began;
                    if (elapsed <= 0)
                    {
                        throw new InvalidOperationException("The timer could not resolve a request; zero-duration samples are not accepted.");
                    }
                    if (capacityPerWorker != 0)
                    {
                        buffers[index][checked((int)counts[index])] = elapsed;
                    }
                    counts[index]++;
                    // Cached checkout/empty transactions can complete synchronously. Without
                    // periodic yielding, the first few workers monopolize thread-pool threads
                    // and the nominal concurrency is not actually exercised.
                    if ((counts[index] & 63) == 0) await Task.Yield();
                }
            }
            catch
            {
                await failureCancellation.CancelAsync();
                throw;
            }
        }, CancellationToken.None)).ToArray();

        try
        {
            await Task.WhenAll(ready.Select(item => item.Task)).WaitAsync(token);
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var gcBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var bytesBefore = GC.GetTotalAllocatedBytes(precise: true);
            var began = Stopwatch.GetTimestamp();
            deadline = checked(began + (long)(seconds * Stopwatch.Frequency));
            start.SetResult();
            await Task.WhenAll(tasks);
            var elapsed = Stopwatch.GetTimestamp() - began;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - bytesBefore;
            var collections = Enumerable.Range(0, 3).Select(generation =>
                GC.CollectionCount(generation) - gcBefore[generation]).ToArray();
            process.Refresh();
            var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            if (counts.Any(count => count == 0))
            {
                throw new InvalidOperationException("At least one worker completed no requests; the requested concurrency was not exercised.");
            }
            return new Window(elapsed, counts.Sum(), allocated, cpu, process.PeakWorkingSet64,
                collections, buffers.Select((buffer, index) => new WorkerSamples(
                    buffer, capacityPerWorker == 0 ? 0 : checked((int)counts[index]))).ToArray());
        }
        finally
        {
            await failureCancellation.CancelAsync();
            start.TrySetCanceled(token);
            // Observe all workers, including when readiness/cancellation fails.
            try { await Task.WhenAll(tasks); }
            catch when (tasks.Any(task => task.IsFaulted || task.IsCanceled)) { }
        }
    }
}

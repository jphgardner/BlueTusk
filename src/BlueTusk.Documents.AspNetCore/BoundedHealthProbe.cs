using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.HostHealth;

// Compiled into the two optional host adapters; no framework or cross-family dependency enters either core.
internal sealed class BoundedHealthProbe(string prefix, TimeSpan timeout, int maximum, Func<CancellationToken, Task<HealthCheckResult>> read,
    Counter<long> results, Histogram<double> duration)
{
    private int _active;
    internal static void Validate(TimeSpan timeout, int maximum)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10) || maximum is < 1 or > 64)
        { throw new ArgumentException("Host health deadlines and concurrency exceed bounded probe limits."); }
    }
    internal async Task<HealthCheckResult> CheckAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        if (Interlocked.Increment(ref _active) > maximum)
        { Interlocked.Decrement(ref _active); return Observe(HealthCheckResult.Degraded(prefix + "_health_probe_saturated"), started); }
        var release = true; Task<HealthCheckResult>? operation = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(timeout);
        try
        {
            operation = read(deadline.Token);
            return Observe(await operation.WaitAsync(deadline.Token).ConfigureAwait(false), started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { Record("cancelled", started); throw; }
        catch (OperationCanceledException)
        { return Observe(HealthCheckResult.Unhealthy(prefix + "_health_probe_timeout"), started); }
        catch (Exception)
        { return Observe(HealthCheckResult.Unhealthy(prefix + "_store_unavailable"), started); }
        finally
        {
            if (operation is not null && !operation.IsCompleted)
            {
                release = false;
                _ = operation.ContinueWith(completed => { _ = completed.Exception; Interlocked.Decrement(ref _active); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            if (release) { Interlocked.Decrement(ref _active); }
        }
    }
    private HealthCheckResult Observe(HealthCheckResult result, long started)
    { Record(result.Status switch { HealthStatus.Healthy => "healthy", HealthStatus.Degraded => "degraded", _ => "unhealthy" }, started); return result; }
    private void Record(string status, long started)
    {
        var tag = new KeyValuePair<string, object?>("status", status);
        results.Add(1, tag); duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tag);
    }
}

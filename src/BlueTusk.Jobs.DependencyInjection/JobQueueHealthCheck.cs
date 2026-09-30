using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Serialization.Metadata;
using BlueTusk.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Jobs.DependencyInjection;

/// <summary>One trusted tenant/queue probe with bounded counts, deadline and redacted readiness results.</summary>
public sealed class JobQueueHealthCheck : IHealthCheck
{
    private readonly PostgreSqlJobStore _store;
    private readonly JobScope _scope;
    private readonly JobHealthCheckOptions _options;
    private int _probing;

    public JobQueueHealthCheck(PostgreSqlJobStore store, JobScope scope, JobHealthCheckOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _store = store;
        _scope = scope;
        _options = options;
    }

    public static JsonTypeInfo<JobReadiness> JsonTypeInfo => JobReadinessJsonContext.Default.JobReadiness;

    public async ValueTask<JobReadiness> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _probing, 1, 0) != 0)
        {
            var busy = new JobReadiness(HealthStatus.Degraded, "jobs.health.probe_busy", null);
            JobHealthTelemetry.Record(busy, TimeSpan.Zero);
            return busy;
        }
        long started = Stopwatch.GetTimestamp();
        using var activity = JobHealthTelemetry.ActivitySource.StartActivity("jobs.readiness");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);
        JobReadiness result;
        try
        {
            var queue = await _store.InspectAsync(_scope, _options.MaximumObserved, deadline.Token).ConfigureAwait(false);
            bool overloaded = queue.PendingCountCapped || queue.RunningCountCapped || queue.ExpiredLeaseCountCapped ||
                queue.PendingObserved > _options.MaximumPending || queue.ExpiredLeasesObserved > _options.MaximumExpiredLeases ||
                queue.OldestReadyAge > _options.MaximumReadyAge;
            result = new(overloaded ? HealthStatus.Degraded : HealthStatus.Healthy,
                overloaded ? "jobs.health.pressure" : "jobs.health.ready", queue);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            result = new(HealthStatus.Unhealthy, "jobs.health.deadline", null);
        }
        catch (BlueTuskException exception) when (exception.SqlState is "42501" or "28P01" or "28000")
        {
            result = new(HealthStatus.Unhealthy, "jobs.health.access_denied", null);
        }
        catch (Exception)
        {
            result = new(HealthStatus.Unhealthy, "jobs.health.store_unavailable", null);
        }
        finally { Volatile.Write(ref _probing, 0); }
        JobHealthTelemetry.Record(result, Stopwatch.GetElapsedTime(started));
        return result;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var data = new Dictionary<string, object>(6, StringComparer.Ordinal);
        if (result.Queue is { } queue)
        {
            data.Add("pending_observed", queue.PendingObserved);
            data.Add("running_observed", queue.RunningObserved);
            data.Add("expired_observed", queue.ExpiredLeasesObserved);
            data.Add("pending_capped", queue.PendingCountCapped);
            data.Add("running_capped", queue.RunningCountCapped);
            data.Add("expired_capped", queue.ExpiredLeaseCountCapped);
        }
        return new(result.Status, result.Code, exception: null, data);
    }
}

public static class JobHealthTelemetry
{
    public const string InstrumentationName = "BlueTusk.Jobs.DependencyInjection";
    public static ActivitySource ActivitySource { get; } = new(InstrumentationName);
    public static Meter Meter { get; } = new(InstrumentationName);
    private static readonly Counter<long> Probes = Meter.CreateCounter<long>("bluetusk.jobs.health.probes");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("bluetusk.jobs.health.duration", "s");
    private static readonly Histogram<long> Pending = Meter.CreateHistogram<long>("bluetusk.jobs.health.pending_observed", "{job}");
    internal static void Record(JobReadiness result, TimeSpan duration)
    {
        string status = result.Status switch { HealthStatus.Healthy => "healthy", HealthStatus.Degraded => "degraded", _ => "unhealthy" };
        Probes.Add(1, new KeyValuePair<string, object?>("status", status));
        Duration.Record(duration.TotalSeconds);
        if (result.Queue is { } queue) { Pending.Record(queue.PendingObserved); }
    }
}

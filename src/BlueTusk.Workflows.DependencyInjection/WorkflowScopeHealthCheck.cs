using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Serialization.Metadata;
using BlueTusk.Data;
using BlueTusk.Jobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Workflows.DependencyInjection;

/// <summary>Bounded scoped readiness of workflow instances and their Jobs dispatch, with stable redacted failures.</summary>
public sealed class WorkflowScopeHealthCheck : IHealthCheck
{
    private readonly PostgreSqlWorkflowStore _store;
    private readonly JobScope _scope;
    private readonly WorkflowHealthCheckOptions _options;
    private int _probing;

    public WorkflowScopeHealthCheck(PostgreSqlWorkflowStore store, JobScope scope, WorkflowHealthCheckOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _store = store;
        _scope = scope;
        _options = options;
    }

    public static JsonTypeInfo<WorkflowReadiness> JsonTypeInfo => WorkflowReadinessJsonContext.Default.WorkflowReadiness;

    public async ValueTask<WorkflowReadiness> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _probing, 1, 0) != 0)
        {
            var busy = new WorkflowReadiness(HealthStatus.Degraded, "workflows.health.probe_busy", null, null);
            WorkflowHealthTelemetry.Record(busy, TimeSpan.Zero);
            return busy;
        }
        long started = Stopwatch.GetTimestamp();
        using var activity = WorkflowHealthTelemetry.ActivitySource.StartActivity("workflows.readiness");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);
        WorkflowReadiness result;
        try
        {
            var scope = await _store.InspectAsync(_scope, _options.MaximumObserved, deadline.Token).ConfigureAwait(false);
            var dispatch = await _store.Jobs.InspectAsync(_scope, _options.MaximumObserved, deadline.Token).ConfigureAwait(false);
            bool overloaded = scope.RunningCountCapped || scope.CompensatingCountCapped || dispatch.PendingCountCapped ||
                dispatch.RunningCountCapped || dispatch.ExpiredLeaseCountCapped || scope.RunningObserved > _options.MaximumRunning ||
                scope.CompensatingObserved > _options.MaximumCompensating || dispatch.PendingObserved > _options.MaximumPendingJobs ||
                dispatch.ExpiredLeasesObserved > _options.MaximumExpiredLeases ||
                (_options.MaximumActiveAge is { } maximumAge && scope.OldestActiveAge > maximumAge);
            result = new(overloaded ? HealthStatus.Degraded : HealthStatus.Healthy,
                overloaded ? "workflows.health.pressure" : "workflows.health.ready", scope, dispatch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            result = new(HealthStatus.Unhealthy, "workflows.health.deadline", null, null);
        }
        catch (BlueTuskException exception) when (exception.SqlState is "42501" or "28P01" or "28000")
        {
            result = new(HealthStatus.Unhealthy, "workflows.health.access_denied", null, null);
        }
        catch (Exception)
        {
            result = new(HealthStatus.Unhealthy, "workflows.health.store_unavailable", null, null);
        }
        finally { Volatile.Write(ref _probing, 0); }
        WorkflowHealthTelemetry.Record(result, Stopwatch.GetElapsedTime(started));
        return result;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var data = new Dictionary<string, object>(8, StringComparer.Ordinal);
        if (result.Scope is { } scope)
        {
            data.Add("running_observed", scope.RunningObserved);
            data.Add("compensating_observed", scope.CompensatingObserved);
            data.Add("running_capped", scope.RunningCountCapped);
            data.Add("compensating_capped", scope.CompensatingCountCapped);
        }
        if (result.Dispatch is { } dispatch)
        {
            data.Add("dispatch_pending_observed", dispatch.PendingObserved);
            data.Add("dispatch_expired_observed", dispatch.ExpiredLeasesObserved);
            data.Add("dispatch_pending_capped", dispatch.PendingCountCapped);
            data.Add("dispatch_expired_capped", dispatch.ExpiredLeaseCountCapped);
        }
        return new(result.Status, result.Code, exception: null, data);
    }
}

public static class WorkflowHealthTelemetry
{
    public const string InstrumentationName = "BlueTusk.Workflows.DependencyInjection";
    public static ActivitySource ActivitySource { get; } = new(InstrumentationName);
    public static Meter Meter { get; } = new(InstrumentationName);
    private static readonly Counter<long> Probes = Meter.CreateCounter<long>("bluetusk.workflows.health.probes");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("bluetusk.workflows.health.duration", "s");
    private static readonly Histogram<long> Running = Meter.CreateHistogram<long>("bluetusk.workflows.health.running_observed", "{workflow}");
    internal static void Record(WorkflowReadiness result, TimeSpan duration)
    {
        string status = result.Status switch { HealthStatus.Healthy => "healthy", HealthStatus.Degraded => "degraded", _ => "unhealthy" };
        Probes.Add(1, new KeyValuePair<string, object?>("status", status));
        Duration.Record(duration.TotalSeconds);
        if (result.Scope is { } scope) { Running.Record(scope.RunningObserved); }
    }
}

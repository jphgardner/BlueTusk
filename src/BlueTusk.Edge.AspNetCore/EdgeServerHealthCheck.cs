using System.Diagnostics;
using System.Diagnostics.Metrics;
using BlueTusk.Edge.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Edge.AspNetCore;

public sealed record EdgeServerHealthCheckOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxConcurrentProbes { get; init; } = 1;
}

/// <summary>A host-authorized fixed-scope probe. No tenant, scope, payload, credential or exception-message telemetry is emitted.</summary>
public sealed class EdgeServerHealthCheck : IHealthCheck
{
    public const string MeterName = "BlueTusk.Edge.Server.Health";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Results = Meter.CreateCounter<long>("bluetusk.edge.health.probes");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("bluetusk.edge.health.duration", "s");
    private readonly PostgreSqlEdgeServerStore _store;
    private readonly EdgeScope _scope;
    private int _activeProbes;

    public EdgeServerHealthCheck(PostgreSqlEdgeServerStore store, EdgeScope scope, EdgeServerHealthCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(scope);
        Options = options ?? new();
        if (Options.Timeout <= TimeSpan.Zero || Options.Timeout > TimeSpan.FromSeconds(10) || Options.MaxConcurrentProbes is < 1 or > 64)
        { throw new ArgumentException("Edge health deadlines and concurrency exceed bounded probe limits.", nameof(options)); }
        _store = store; _scope = scope;
    }
    public EdgeServerHealthCheckOptions Options { get; }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var started = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _activeProbes) > Options.MaxConcurrentProbes)
        {
            Interlocked.Decrement(ref _activeProbes);
            return Observe(HealthCheckResult.Degraded("edge_health_probe_saturated"), started);
        }
        var release = true;
        Task<EdgeServerHealthSnapshot>? operation = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Options.Timeout);
        try
        {
            operation = _store.ReadHealthAsync(_scope, deadline.Token).AsTask();
            var state = await operation.WaitAsync(deadline.Token).ConfigureAwait(false);
            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["head_position"] = state.HeadPosition,
                ["replay_floor"] = state.ReplayFloor,
                ["record_count"] = state.RecordCount,
                ["record_bytes"] = state.RecordBytes,
                ["receipt_count"] = state.ReceiptCount,
                ["change_count"] = state.ChangeCount,
                ["change_bytes"] = state.ChangeBytes,
                ["active_snapshots"] = state.ActiveSnapshots,
            };
            return Observe(state.MutationCapacityReached ? HealthCheckResult.Degraded("edge_mutation_capacity_reached", data: data)
                : state.SnapshotCapacityReached ? HealthCheckResult.Degraded("edge_snapshot_capacity_reached", data: data)
                : HealthCheckResult.Healthy("edge_scope_ready", data), started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveCancellation(started); throw;
        }
        catch (OperationCanceledException)
        { return Observe(HealthCheckResult.Unhealthy("edge_health_probe_timeout"), started); }
        catch (EdgeScopeMismatchException)
        { return Observe(HealthCheckResult.Unhealthy("edge_scope_unavailable"), started); }
        catch (Exception)
        {
            // HealthCheckService would log exception messages if allowed to escape or attached to the result.
            return Observe(HealthCheckResult.Unhealthy("edge_store_unavailable"), started);
        }
        finally
        {
            if (operation is not null && !operation.IsCompleted)
            {
                // Keep admission reserved until even an uncooperative data source finishes; observe late faults without logging them.
                release = false;
                _ = operation.ContinueWith(completed => { _ = completed.Exception; Interlocked.Decrement(ref _activeProbes); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            if (release) { Interlocked.Decrement(ref _activeProbes); }
        }
    }
    private static HealthCheckResult Observe(HealthCheckResult result, long started)
    {
        var status = result.Status switch { HealthStatus.Healthy => "healthy", HealthStatus.Degraded => "degraded", _ => "unhealthy" };
        var tag = new KeyValuePair<string, object?>("status", status);
        Results.Add(1, tag); Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tag); return result;
    }
    private static void ObserveCancellation(long started)
    {
        var tag = new KeyValuePair<string, object?>("status", "cancelled");
        Results.Add(1, tag); Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tag);
    }
}

public static class EdgeServerHealthCheckExtensions
{
    /// <summary>Registers a fixed host-selected scope. The host maps and authorizes its own operator health endpoint.</summary>
    public static IHealthChecksBuilder AddBlueTuskEdgeServer(this IHealthChecksBuilder builder, string name,
        PostgreSqlEdgeServerStore store, EdgeScope scope, EdgeServerHealthCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || name.Contains('\0', StringComparison.Ordinal)) { throw new ArgumentException("Health check name exceeds its bounded identity limit.", nameof(name)); }
        var check = new EdgeServerHealthCheck(store, scope, options);
        return builder.Add(new HealthCheckRegistration(name, _ => check, HealthStatus.Unhealthy, ["ready"]));
    }
}

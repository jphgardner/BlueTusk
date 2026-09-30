using System.Diagnostics.Metrics;
using BlueTusk.HostHealth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Search.AspNetCore;

public sealed record SearchStoreHealthCheckOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxConcurrentProbes { get; init; } = 1;
}

public sealed class SearchStoreHealthCheck : IHealthCheck
{
    public const string MeterName = "BlueTusk.Search.Health";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Results = Meter.CreateCounter<long>("bluetusk.search.health.probes");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("bluetusk.search.health.duration", "s");
    private readonly BoundedHealthProbe _probe;
    public SearchStoreHealthCheck(PostgreSqlSearchStore store, SearchScope scope, SearchStoreHealthCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(scope);
        Options = options ?? new(); BoundedHealthProbe.Validate(Options.Timeout, Options.MaxConcurrentProbes);
        _probe = new("search", Options.Timeout, Options.MaxConcurrentProbes, async cancellationToken =>
        {
            var state = await store.ReadHealthAsync(scope, cancellationToken).ConfigureAwait(false);
            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            { ["storage_version"] = state.StorageVersion, ["has_documents"] = state.HasDocuments, ["observed_retained_queries"] = state.ObservedRetainedQueries, ["observed_active_queries"] = state.ObservedActiveQueries };
            return state.QueryCapacityReached ? HealthCheckResult.Degraded("search_query_capacity_reached", data: data) : HealthCheckResult.Healthy("search_scope_ready", data);
        }, Results, Duration);
    }
    public SearchStoreHealthCheckOptions Options { get; }
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) => _probe.CheckAsync(context, cancellationToken);
}

public static class SearchStoreHealthCheckExtensions
{
    public static IHealthChecksBuilder AddBlueTuskSearch(this IHealthChecksBuilder builder, string name, PostgreSqlSearchStore store,
        SearchScope scope, SearchStoreHealthCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || name.Contains('\0', StringComparison.Ordinal)) { throw new ArgumentException("Health check name exceeds its bounded identity limit.", nameof(name)); }
        var check = new SearchStoreHealthCheck(store, scope, options);
        return builder.Add(new HealthCheckRegistration(name, _ => check, HealthStatus.Unhealthy, ["ready"]));
    }
}

using System.Diagnostics.Metrics;
using BlueTusk.HostHealth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Documents.AspNetCore;

public sealed record DocumentStoreHealthCheckOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxConcurrentProbes { get; init; } = 1;
}

public sealed class DocumentStoreHealthCheck : IHealthCheck
{
    public const string MeterName = "BlueTusk.Documents.Health";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Results = Meter.CreateCounter<long>("bluetusk.documents.health.probes");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("bluetusk.documents.health.duration", "s");
    private readonly BoundedHealthProbe _probe;
    public DocumentStoreHealthCheck(DocumentStore store, string tenant, string collection, DocumentStoreHealthCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        // Apply the core identity contract without opening a database connection.
        using var session = store.OpenSession(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        if (collection.Contains('\0', StringComparison.Ordinal) || System.Text.Encoding.UTF8.GetByteCount(collection) > 256) { throw new ArgumentException("Collection exceeds its bounded identity contract.", nameof(collection)); }
        Options = options ?? new(); BoundedHealthProbe.Validate(Options.Timeout, Options.MaxConcurrentProbes);
        _probe = new("documents", Options.Timeout, Options.MaxConcurrentProbes, async cancellationToken =>
        {
            var state = await store.ReadHealthAsync(tenant, collection, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("documents_scope_ready", new Dictionary<string, object>(StringComparer.Ordinal)
            { ["storage_version"] = state.StorageVersion, ["max_document_bytes"] = state.MaxDocumentBytes, ["has_documents"] = state.HasDocuments });
        }, Results, Duration);
    }
    public DocumentStoreHealthCheckOptions Options { get; }
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) => _probe.CheckAsync(context, cancellationToken);
}

public static class DocumentStoreHealthCheckExtensions
{
    public static IHealthChecksBuilder AddBlueTuskDocuments(this IHealthChecksBuilder builder, string name, DocumentStore store,
        string tenant, string collection, DocumentStoreHealthCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || name.Contains('\0', StringComparison.Ordinal)) { throw new ArgumentException("Health check name exceeds its bounded identity limit.", nameof(name)); }
        var check = new DocumentStoreHealthCheck(store, tenant, collection, options);
        return builder.Add(new HealthCheckRegistration(name, _ => check, HealthStatus.Unhealthy, ["ready"]));
    }
}

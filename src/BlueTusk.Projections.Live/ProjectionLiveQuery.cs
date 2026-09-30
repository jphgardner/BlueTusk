using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BlueTusk.Live;

namespace BlueTusk.Projections.Live;

public sealed record ProjectionLiveRow<T>(string Key, int PublishedVersion, T Value);

public sealed record ProjectionLiveQueryOptions
{
    public int MaximumDocuments { get; init; } = 256;
    public int MaximumPayloadBytes { get; init; } = 8_388_608;
    public string? AfterKey { get; init; }
    public string? ThroughKey { get; init; }
}

/// <summary>A server-registered query over one bounded ordinal key window in an authenticated tenant.</summary>
public sealed class ProjectionLiveQuery<T>
{
    private readonly PostgreSqlProjectionStore _store;
    private readonly string _projectionName;
    private readonly string _tenantId;
    private readonly LiveSecurityScope _securityScope;
    private readonly ProjectionLiveQueryOptions _options;
    private readonly JsonTypeInfo<T> _documentTypeInfo;
    private readonly Func<T, bool>? _predicate;
    private readonly AsyncLocal<PublicationCaptureScope?> _publicationCapture = new();
    private ProjectionPublication _publication = new(null, 0);

    public ProjectionLiveQuery(PostgreSqlProjectionStore store, string projectionName, string tenantId,
        LiveSecurityScope securityScope, string name, string databaseIdentity, string queryVersion,
        JsonTypeInfo<T> documentTypeInfo, ProjectionLiveQueryOptions? options = null, Func<T, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(securityScope);
        ArgumentNullException.ThrowIfNull(documentTypeInfo);
        _options = options ?? new ProjectionLiveQueryOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumDocuments);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaximumDocuments, 65_536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaximumPayloadBytes, 67_108_864);
        _store = store;
        _projectionName = projectionName;
        _tenantId = tenantId;
        _securityScope = securityScope;
        _documentTypeInfo = documentTypeInfo;
        _predicate = predicate;
        var shape = Encoding.UTF8.GetBytes($"{projectionName}\n{tenantId}\n{_options.MaximumDocuments}\n{_options.MaximumPayloadBytes}\n{_options.AfterKey}\n{_options.ThroughKey}");
        Plan = new LiveQueryPlan<ProjectionLiveRow<T>, string>(name, databaseIdentity,
            LiveQueryFingerprint.Create(name, queryVersion, shape),
            LiveQueryCapabilities.SingleTable | LiveQueryCapabilities.TenantFilter | LiveQueryCapabilities.ParameterizedPredicate |
            LiveQueryCapabilities.DeterministicOrdering | LiveQueryCapabilities.BoundedTake,
            [ProjectionLiveInvalidationLog.Dependency(projectionName)], [],
            _options.MaximumDocuments, ExecuteAsync, static row => row.Key, keyComparer: StringComparer.Ordinal);
        InvalidationLog = new ProjectionLiveInvalidationLog(store, projectionName, databaseIdentity);
    }

    public LiveQueryPlan<ProjectionLiveRow<T>, string> Plan { get; }
    public ProjectionLiveInvalidationLog InvalidationLog { get; }
    public ProjectionPublication LastPublication => Volatile.Read(ref _publication);

    public LiveQuerySession<ProjectionLiveRow<T>, string> CreateSession(LiveQuerySessionOptions? options = null) =>
        new(Plan, Plan.Bind(new Dictionary<string, object?>()), _securityScope, InvalidationLog, options: options);

    internal ValueTask<ProjectionPublication> ReadPublicationAsync(CancellationToken cancellationToken) =>
        _store.ReadPublicationAsync(_projectionName, cancellationToken);

    internal PublicationCaptureScope CapturePublication() => CreateCapture(null, requireVersion: false);

    internal PublicationCaptureScope RequirePublishedVersion(int? version) =>
        CreateCapture(version, requireVersion: true);

    private PublicationCaptureScope CreateCapture(int? version, bool requireVersion)
    {
        var scope = new PublicationCaptureScope(_publicationCapture, _publicationCapture.Value,
            version, requireVersion);
        _publicationCapture.Value = scope;
        return scope;
    }

    private async ValueTask<IReadOnlyList<ProjectionLiveRow<T>>> ExecuteAsync(LiveQueryExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.SecurityScope != _securityScope || context.Arguments.Values.Count != 0)
        {
            throw new UnauthorizedAccessException("The projection query is bound to a different authenticated tenant/security scope.");
        }
        var page = await _store.ReadActivePageAsync(_projectionName, _tenantId, _options.MaximumDocuments,
            _options.MaximumPayloadBytes, afterKey: _options.AfterKey, throughKey: _options.ThroughKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        // The page and publication belong to one statement snapshot. Reject a version switch
        // before Live can persist a row diff; the subscription will publish a reset instead.
        var capture = _publicationCapture.Value;
        capture?.Record(page.Publication);
        if (capture is { RequireVersion: true } && page.Publication.Version != capture.ExpectedVersion)
        {
            throw new ProjectionLivePublicationChangedException();
        }
        var rows = new List<ProjectionLiveRow<T>>(page.Documents.Count);
        foreach (var document in page.Documents)
        {
            var value = JsonSerializer.Deserialize(document.Payload.Span, _documentTypeInfo)
                ?? throw new JsonException("A published projection document cannot deserialize to null.");
            if (_predicate is null || _predicate(value))
            {
                rows.Add(new ProjectionLiveRow<T>(document.Key, page.Publication.Version!.Value, value));
            }
        }
        Volatile.Write(ref _publication, page.Publication);
        return rows.AsReadOnly();
    }

    internal sealed class PublicationCaptureScope(AsyncLocal<PublicationCaptureScope?> slot,
        PublicationCaptureScope? previous, int? expectedVersion, bool requireVersion) : IDisposable
    {
        internal int? ExpectedVersion { get; } = expectedVersion;
        internal bool RequireVersion { get; } = requireVersion;
        internal ProjectionPublication? ObservedPublication { get; private set; }

        internal void Record(ProjectionPublication publication) => ObservedPublication = publication;
        public void Dispose() => slot.Value = previous;
    }
}

internal sealed class ProjectionLivePublicationChangedException : Exception { }

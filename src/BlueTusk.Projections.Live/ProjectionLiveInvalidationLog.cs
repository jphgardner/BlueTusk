using BlueTusk.Live;

namespace BlueTusk.Projections.Live;

/// <summary>Coalesces committed materialized projection revisions. Streams remains the only source CDC seam.</summary>
public sealed class ProjectionLiveInvalidationLog : ILiveInvalidationLog
{
    private readonly PostgreSqlProjectionStore _store;
    private readonly string _projectionName;
    private readonly string _databaseIdentity;

    public ProjectionLiveInvalidationLog(PostgreSqlProjectionStore store, string projectionName, string databaseIdentity)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        _store = store;
        _projectionName = projectionName;
        _databaseIdentity = databaseIdentity;
    }

    public async ValueTask<LiveInvalidationCursor> GetCurrentCursorAsync(string databaseIdentity, CancellationToken cancellationToken = default)
    {
        Validate(databaseIdentity);
        return new LiveInvalidationCursor((await _store.ReadPublicationAsync(_projectionName, cancellationToken).ConfigureAwait(false)).Revision);
    }

    public ValueTask<bool> HasChangesAsync(string databaseIdentity, IReadOnlyCollection<LiveTableDependency> dependencies,
        LiveInvalidationCursor afterExclusive, LiveInvalidationCursor throughInclusive, CancellationToken cancellationToken = default)
    {
        Validate(databaseIdentity);
        ArgumentNullException.ThrowIfNull(dependencies);
        cancellationToken.ThrowIfCancellationRequested();
        if (dependencies.Count != 1 || dependencies.First() != Dependency(_projectionName))
        {
            throw new ArgumentException("Projection invalidation requires its one registered materialized dependency.", nameof(dependencies));
        }
        if (afterExclusive.Value < 0 || throughInclusive < afterExclusive) { throw new ArgumentOutOfRangeException(nameof(throughInclusive)); }
        return ValueTask.FromResult(throughInclusive > afterExclusive);
    }

    internal static LiveTableDependency Dependency(string name) => new("bluetusk_projections", name);

    private void Validate(string databaseIdentity)
    {
        if (!string.Equals(databaseIdentity, _databaseIdentity, StringComparison.Ordinal))
        {
            throw new ArgumentException("The Live database identity differs from the registered projection.", nameof(databaseIdentity));
        }
    }
}

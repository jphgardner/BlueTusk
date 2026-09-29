using System.Text.Json.Serialization.Metadata;
using BlueTusk.Live;

namespace BlueTusk.Projections.Live;

/// <summary>Uses Live's existing replay/fan-out protocol and persists an authoritative reset at version cutover.</summary>
public sealed class ProjectionLiveSubscription<T> : ILiveSharedSubscription
{
    private readonly ProjectionLiveQuery<T> _query;
    private readonly LiveSharedSubscription<ProjectionLiveRow<T>, string> _shared;
    private readonly ProjectionLivePublisher? _publisher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int? _publishedVersion;
    private bool _started;
    private bool _ownershipLost;
    private int _disposed;

    public ProjectionLiveSubscription(ProjectionLiveQuery<T> query, ILiveReplayStore replayStore,
        JsonTypeInfo<LiveResultEvent<ProjectionLiveRow<T>, string>> eventTypeInfo,
        LiveQuerySessionOptions? sessionOptions = null, LiveSharedSubscriptionOptions? subscriptionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        _query = query;
        _shared = LiveSharedSubscriptions.CreateWithJsonMetadata(query.CreateSession(sessionOptions), replayStore, eventTypeInfo, subscriptionOptions);
        _publisher = replayStore as ProjectionLivePublisher;
        if (_publisher is not null && _publisher.Lease.Identity != _shared.Identity) { throw new ArgumentException("The publisher lease belongs to another authorized query identity.", nameof(replayStore)); }
    }

    public LiveSubscriptionIdentity Identity => _shared.Identity;
    public LiveSharedSubscriptionStatus Status => _shared.Status;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);
            await _shared.StartAsync(cancellationToken).ConfigureAwait(false);
            _publishedVersion = _query.LastPublication.Version;
            _started = true;
        }
        catch (ProjectionLivePublisherFencedException) { await LoseOwnershipAsync().ConfigureAwait(false); throw; }
        finally { _gate.Release(); }
    }

    public async ValueTask<int> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch (ProjectionLivePublisherFencedException) { await LoseOwnershipAsync().ConfigureAwait(false); throw; }
        finally { _gate.Release(); }
    }

    public async ValueTask<LiveSubscriptionConnectResult> ConnectAsync(long afterSequence, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started) { _ = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false); }
            return await _shared.ConnectAsync(afterSequence, cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectionLivePublisherFencedException) { await LoseOwnershipAsync().ConfigureAwait(false); throw; }
        finally { _gate.Release(); }
    }

    public async ValueTask<LiveSubscriptionConnectResult> ConnectWithTokenAsync(string resumeToken, LiveResumeTokenProtector tokenProtector,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started) { _ = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false); }
            return await _shared.ConnectWithTokenAsync(resumeToken, tokenProtector, cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectionLivePublisherFencedException) { await LoseOwnershipAsync().ConfigureAwait(false); throw; }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _shared.DisposeAsync().ConfigureAwait(false);
            if (_publisher is not null) { await _publisher.DisposeAsync().ConfigureAwait(false); }
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    private async ValueTask<int> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        await EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);
        var publication = await _query.ReadPublicationAsync(cancellationToken).ConfigureAwait(false);
        return await RefreshFromObservedPublicationAsync(publication, cancellationToken).ConfigureAwait(false);
    }

    // The split keeps the preliminary read/query interleaving independently testable.
    internal async ValueTask<int> RefreshFromObservedPublicationAsync(ProjectionPublication publication,
        CancellationToken cancellationToken = default)
    {
        int count;
        if (publication.Version != _publishedVersion)
        {
            count = await _shared.ResetAsync(LiveResetReason.SchemaChanged, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                using (_query.RequirePublishedVersion(_publishedVersion))
                {
                    count = await _shared.RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (ProjectionLivePublicationChangedException)
            {
                count = await _shared.ResetAsync(LiveResetReason.SchemaChanged, cancellationToken).ConfigureAwait(false);
            }
        }
        _publishedVersion = _query.LastPublication.Version;
        return count;
    }

    private async ValueTask EnsureOwnerAsync(CancellationToken cancellationToken)
    {
        if (_ownershipLost) { throw new ProjectionLivePublisherFencedException(); }
        if (_publisher is null) { return; }
        try { await _publisher.EnsureActiveAsync(cancellationToken).ConfigureAwait(false); }
        catch (ProjectionLivePublisherFencedException) { await LoseOwnershipAsync().ConfigureAwait(false); throw; }
    }

    private async ValueTask LoseOwnershipAsync()
    { _ownershipLost = true; await _shared.DisposeAsync().ConfigureAwait(false); }
}

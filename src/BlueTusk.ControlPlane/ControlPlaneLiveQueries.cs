using System.Security.Cryptography;
using System.Text;
using BlueTusk.Live;

namespace BlueTusk.ControlPlane;

public sealed class ControlPlaneLiveOverview
{
    public ControlPlaneLiveOverview(
        DateTimeOffset observedAt,
        ControlPlaneLiveRegistrySnapshot registry,
        IReadOnlyList<ControlPlaneLiveSubscriptionSnapshot> subscriptions)
    {
        ObservedAt = observedAt;
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
    }

    public DateTimeOffset ObservedAt { get; }

    public ControlPlaneLiveRegistrySnapshot Registry { get; }

    public IReadOnlyList<ControlPlaneLiveSubscriptionSnapshot> Subscriptions { get; }
}

public sealed class ControlPlaneLiveRegistrySnapshot
{
    public ControlPlaneLiveRegistrySnapshot(
        int sharedSubscriptions,
        int maximumSharedSubscriptions,
        long quotaRejections)
    {
        SharedSubscriptions = sharedSubscriptions;
        MaximumSharedSubscriptions = maximumSharedSubscriptions;
        QuotaRejections = quotaRejections;
    }

    public int SharedSubscriptions { get; }

    public int MaximumSharedSubscriptions { get; }

    public long QuotaRejections { get; }
}

public sealed class ControlPlaneLiveSubscriptionSnapshot
{
    public ControlPlaneLiveSubscriptionSnapshot(
        string subscriptionFingerprint,
        string queryPlanFingerprint,
        string parameterFingerprint,
        string securityScopeLabel,
        string authorizationPolicyVersion,
        int resultLimit,
        bool isStarted,
        int subscriberCount,
        double fanOutRatio,
        long publishedEvents,
        long fanOutDeliveries,
        long persistedSequence,
        long replayBytesAppended,
        long replayedEvents,
        long connectionOpenAttempts,
        long connectedClients,
        long resumeAttempts,
        long resumeRejections,
        long replayRejections,
        long quotaRejections,
        long slowClientDisconnects,
        string? lastDisconnectCode,
        long invalidationCursor,
        long invalidationHead,
        long? invalidationLag,
        string? lagDiagnosticCode,
        long authoritativeQueryCount,
        long coalescedInvalidationCount,
        int resultCount)
    {
        SubscriptionFingerprint = subscriptionFingerprint;
        QueryPlanFingerprint = queryPlanFingerprint;
        ParameterFingerprint = parameterFingerprint;
        SecurityScopeLabel = securityScopeLabel;
        AuthorizationPolicyVersion = authorizationPolicyVersion;
        ResultLimit = resultLimit;
        IsStarted = isStarted;
        SubscriberCount = subscriberCount;
        FanOutRatio = fanOutRatio;
        PublishedEvents = publishedEvents;
        FanOutDeliveries = fanOutDeliveries;
        PersistedSequence = persistedSequence;
        ReplayBytesAppended = replayBytesAppended;
        ReplayedEvents = replayedEvents;
        ConnectionOpenAttempts = connectionOpenAttempts;
        ConnectedClients = connectedClients;
        ResumeAttempts = resumeAttempts;
        ResumeRejections = resumeRejections;
        ReplayRejections = replayRejections;
        QuotaRejections = quotaRejections;
        SlowClientDisconnects = slowClientDisconnects;
        LastDisconnectCode = lastDisconnectCode;
        InvalidationCursor = invalidationCursor;
        InvalidationHead = invalidationHead;
        InvalidationLag = invalidationLag;
        LagDiagnosticCode = lagDiagnosticCode;
        AuthoritativeQueryCount = authoritativeQueryCount;
        CoalescedInvalidationCount = coalescedInvalidationCount;
        ResultCount = resultCount;
    }

    public string SubscriptionFingerprint { get; }

    public string QueryPlanFingerprint { get; }

    public string ParameterFingerprint { get; }

    public string SecurityScopeLabel { get; }

    public string AuthorizationPolicyVersion { get; }

    public int ResultLimit { get; }

    public bool IsStarted { get; }

    public int SubscriberCount { get; }

    public double FanOutRatio { get; }

    public long PublishedEvents { get; }

    public long FanOutDeliveries { get; }

    public long PersistedSequence { get; }

    public long ReplayBytesAppended { get; }

    public long ReplayedEvents { get; }

    public long ConnectionOpenAttempts { get; }

    public long ConnectedClients { get; }

    public long ResumeAttempts { get; }

    public long ResumeRejections { get; }

    public long ReplayRejections { get; }

    public long QuotaRejections { get; }

    public long SlowClientDisconnects { get; }

    public string? LastDisconnectCode { get; }

    public long InvalidationCursor { get; }

    public long InvalidationHead { get; }

    public long? InvalidationLag { get; }

    public string? LagDiagnosticCode { get; }

    public long AuthoritativeQueryCount { get; }

    public long CoalescedInvalidationCount { get; }

    public int ResultCount { get; }
}

public interface IControlPlaneLiveQueryService
{
    ValueTask<ControlPlaneLiveOverview> GetLiveOverviewAsync(
        CancellationToken cancellationToken = default);
}

public interface IControlPlaneLiveScopeRedactor
{
    string Redact(string securityScope);
}

public sealed class FingerprintControlPlaneLiveScopeRedactor : IControlPlaneLiveScopeRedactor
{
    public string Redact(string securityScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityScope);
        var separator = securityScope.IndexOf(':', StringComparison.Ordinal);
        var category = separator is > 0 and <= 32 &&
                       IsSafeCategory(securityScope.AsSpan(0, separator))
            ? securityScope[..separator]
            : "scope";
        var fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(securityScope)));
        return category + ":#" + fingerprint[..12];
    }

    private static bool IsSafeCategory(ReadOnlySpan<char> category)
    {
        foreach (var character in category)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class HostedLiveControlPlaneQueryService : IControlPlaneLiveQueryService
{
    private readonly LiveSharedSubscriptionRegistry _registry;
    private readonly ILiveInvalidationLog _invalidationLog;
    private readonly IControlPlaneLiveScopeRedactor _scopeRedactor;
    private readonly TimeProvider _timeProvider;

    public HostedLiveControlPlaneQueryService(
        LiveSharedSubscriptionRegistry registry,
        ILiveInvalidationLog invalidationLog,
        IControlPlaneLiveScopeRedactor? scopeRedactor = null,
        TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _invalidationLog = invalidationLog ?? throw new ArgumentNullException(nameof(invalidationLog));
        _scopeRedactor = scopeRedactor ?? new FingerprintControlPlaneLiveScopeRedactor();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ControlPlaneLiveOverview> GetLiveOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        var statuses = _registry.GetStatuses();
        var subscriptions = new ControlPlaneLiveSubscriptionSnapshot[statuses.Count];
        for (var index = 0; index < statuses.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = statuses[index];
            var head = await _invalidationLog.GetCurrentCursorAsync(
                status.Identity.DatabaseIdentity,
                cancellationToken).ConfigureAwait(false);
            var cursor = status.QuerySession.Cursor;
            var regressed = head < cursor;
            subscriptions[index] = new ControlPlaneLiveSubscriptionSnapshot(
                status.Identity.Fingerprint,
                status.Identity.QueryPlanFingerprint,
                status.Identity.ParameterFingerprint,
                _scopeRedactor.Redact(status.Identity.SecurityScope),
                status.Identity.AuthorizationPolicyVersion,
                status.Identity.ResultLimit,
                status.IsStarted,
                status.SubscriberCount,
                status.PublishedEvents == 0
                    ? 0
                    : (double)status.FanOutDeliveries / status.PublishedEvents,
                status.PublishedEvents,
                status.FanOutDeliveries,
                status.PersistedSequence,
                status.ReplayBytesAppended,
                status.ReplayedEvents,
                status.ConnectionOpenAttempts,
                status.ConnectedClients,
                status.ResumeAttempts,
                status.ResumeRejections,
                status.ReplayRejections,
                status.QuotaRejections,
                status.SlowClientDisconnects,
                status.LastDisconnectCode,
                cursor.Value,
                head.Value,
                regressed ? null : head.Value - cursor.Value,
                regressed ? "invalidation-cursor-regressed" : null,
                status.QuerySession.AuthoritativeQueryCount,
                status.QuerySession.CoalescedInvalidationCount,
                status.QuerySession.ResultCount);
        }

        var registry = _registry.Status;
        return new ControlPlaneLiveOverview(
            _timeProvider.GetUtcNow(),
            new ControlPlaneLiveRegistrySnapshot(
                registry.Count,
                registry.MaximumSharedSubscriptions,
                registry.QuotaRejections),
            subscriptions);
    }
}

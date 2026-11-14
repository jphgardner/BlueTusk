using System.Security.Claims;
using BlueTusk.ControlPlane;

namespace BlueTusk.Studio.ControlPlane;

/// <summary>Host-selected services and identities authorized for exactly one principal. Studio never derives permissions from fingerprints.</summary>
public sealed record StudioControlPlaneScope(
    IControlPlaneLiveQueryService LiveQueries,
    ControlPlaneOperationExecutor Operations,
    ControlPlaneActor Actor,
    IReadOnlySet<string> SubscriptionFingerprints,
    IReadOnlyDictionary<string, string> ReplayTargets);

public interface IStudioControlPlaneScopeResolver
{
    ValueTask<StudioControlPlaneScope> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

public sealed record StudioControlPlaneOptions
{
    public required string ReplayPolicy { get; init; }
    public int MaximumSubscriptions { get; init; } = 100;
    public int MaximumAuthorizedSubscriptions { get; init; } = 10_000;
    public int MaximumConcurrentOperations { get; init; } = 4;
    public int MaximumReplyBytes { get; init; } = 1024 * 1024;
    public int TimeoutSeconds { get; init; } = 10;

    internal void Validate()
    {
        BlueTuskGuard.NotNullOrWhiteSpace(ReplayPolicy);
        BlueTuskGuard.NotLessThan(MaximumSubscriptions, 1);
        BlueTuskGuard.NotGreaterThan(MaximumSubscriptions, 1000);
        BlueTuskGuard.NotLessThan(MaximumAuthorizedSubscriptions, MaximumSubscriptions);
        BlueTuskGuard.NotGreaterThan(MaximumAuthorizedSubscriptions, 100_000);
        BlueTuskGuard.NotLessThan(MaximumConcurrentOperations, 1);
        BlueTuskGuard.NotGreaterThan(MaximumConcurrentOperations, 64);
        BlueTuskGuard.NotLessThan(MaximumReplyBytes, 1024);
        BlueTuskGuard.NotGreaterThan(MaximumReplyBytes, 16 * 1024 * 1024);
        BlueTuskGuard.NotLessThan(TimeoutSeconds, 1);
        BlueTuskGuard.NotGreaterThan(TimeoutSeconds, 30);
    }
}

public sealed record StudioReplayRequest(Guid OperationId, string Target, string Confirmation, string Reason);

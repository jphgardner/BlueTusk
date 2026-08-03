namespace BlueTusk.ControlPlane;

public enum ControlPlaneRole
{
    Viewer,
    Operator,
    Administrator,
}

public enum ControlPlaneOperationKind
{
    PauseSource,
    ResumeSource,
    PauseConsumerGroup,
    ResumeConsumerGroup,
    RetryPipeline,
    ReconcilePipeline,
    RebuildPipeline,
    RemoveConsumerGroup,
    RewindCheckpoint,
    DeleteSlot,
}

public enum ControlPlaneAuditStatus
{
    Denied,
    Rejected,
    Requested,
    Succeeded,
    Failed,
}

public sealed record ControlPlaneActor(
    string ActorId,
    IReadOnlySet<ControlPlaneRole> Roles);

public sealed record ControlPlaneOperationRequest(
    Guid OperationId,
    ControlPlaneOperationKind Kind,
    string Target,
    string Confirmation,
    string Reason);

public sealed record ControlPlaneOperationPolicy(
    ControlPlaneRole RequiredRole,
    string RequiredConfirmation,
    bool IsDestructive);

public sealed record ControlPlaneAuditRecord(
    Guid OperationId,
    DateTimeOffset OccurredAt,
    string ActorId,
    ControlPlaneOperationKind Kind,
    string Target,
    ControlPlaneAuditStatus Status,
    string Reason,
    string? DetailCode);

public static class ControlPlaneOperationPolicies
{
    public static ControlPlaneOperationPolicy Get(
        ControlPlaneOperationKind kind,
        string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var destructive = kind is
            ControlPlaneOperationKind.RemoveConsumerGroup or
            ControlPlaneOperationKind.RewindCheckpoint or
            ControlPlaneOperationKind.DeleteSlot;
        return new ControlPlaneOperationPolicy(
            destructive ? ControlPlaneRole.Administrator : ControlPlaneRole.Operator,
            kind + ":" + target,
            destructive);
    }
}

public interface IControlPlaneAuthorizer
{
    ValueTask<bool> AuthorizeAsync(
        ControlPlaneActor actor,
        ControlPlaneRole requiredRole,
        CancellationToken cancellationToken = default);
}

public interface IControlPlaneAuditStore
{
    ValueTask AppendAsync(
        ControlPlaneAuditRecord record,
        CancellationToken cancellationToken = default);
}

public interface IControlPlaneOperationHandler
{
    ValueTask ExecuteAsync(
        ControlPlaneOperationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class RoleControlPlaneAuthorizer : IControlPlaneAuthorizer
{
    public ValueTask<bool> AuthorizeAsync(
        ControlPlaneActor actor,
        ControlPlaneRole requiredRole,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(actor.Roles);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(requiredRole))
        {
            throw new ArgumentOutOfRangeException(nameof(requiredRole));
        }

        return ValueTask.FromResult(
            actor.Roles.All(Enum.IsDefined) &&
            actor.Roles.Any(role => role >= requiredRole));
    }
}

public sealed class ControlPlaneOperationExecutor
{
    private readonly IControlPlaneAuthorizer _authorizer;
    private readonly IControlPlaneAuditStore _auditStore;
    private readonly IControlPlaneOperationHandler _handler;
    private readonly TimeProvider _timeProvider;

    public ControlPlaneOperationExecutor(
        IControlPlaneAuthorizer authorizer,
        IControlPlaneAuditStore auditStore,
        IControlPlaneOperationHandler handler,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(auditStore);
        ArgumentNullException.ThrowIfNull(handler);
        _authorizer = authorizer;
        _auditStore = auditStore;
        _handler = handler;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask ExecuteAsync(
        ControlPlaneActor actor,
        ControlPlaneOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(actor, request);
        var policy = ControlPlaneOperationPolicies.Get(request.Kind, request.Target);
        if (!await _authorizer.AuthorizeAsync(actor, policy.RequiredRole, cancellationToken)
                .ConfigureAwait(false))
        {
            await AuditAsync(actor, request, ControlPlaneAuditStatus.Denied, "role-denied", cancellationToken)
                .ConfigureAwait(false);
            throw new ControlPlaneAuthorizationException(
                $"Operation '{request.Kind}' requires the '{policy.RequiredRole}' role.");
        }

        if (!string.Equals(
                request.Confirmation,
                policy.RequiredConfirmation,
                StringComparison.Ordinal))
        {
            await AuditAsync(
                actor,
                request,
                ControlPlaneAuditStatus.Rejected,
                "confirmation-mismatch",
                cancellationToken).ConfigureAwait(false);
            throw new ControlPlaneConfirmationException(
                "The operation confirmation does not exactly match the required value.");
        }

        await AuditAsync(actor, request, ControlPlaneAuditStatus.Requested, null, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await _handler.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AuditFailureAsync(actor, request, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                await AuditAsync(
                    actor,
                    request,
                    ControlPlaneAuditStatus.Failed,
                    exception.GetType().Name,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception auditException)
            {
                throw new ControlPlaneOperationException(
                    "The operation and its completion audit both failed.",
                    new AggregateException(exception, auditException));
            }

            throw;
        }

        try
        {
            await AuditAsync(actor, request, ControlPlaneAuditStatus.Succeeded, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ControlPlaneOperationException(
                "The operation completed but its success audit could not be stored; reconcile using the operation ID.",
                exception);
        }
    }

    private async ValueTask AuditFailureAsync(
        ControlPlaneActor actor,
        ControlPlaneOperationRequest request,
        string detailCode)
    {
        try
        {
            await AuditAsync(
                actor,
                request,
                ControlPlaneAuditStatus.Failed,
                detailCode,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ControlPlaneOperationException(
                "The cancelled operation could not write its completion audit.",
                exception);
        }
    }

    private ValueTask AuditAsync(
        ControlPlaneActor actor,
        ControlPlaneOperationRequest request,
        ControlPlaneAuditStatus status,
        string? detailCode,
        CancellationToken cancellationToken) =>
        _auditStore.AppendAsync(
            new ControlPlaneAuditRecord(
                request.OperationId,
                _timeProvider.GetUtcNow(),
                actor.ActorId,
                request.Kind,
                request.Target,
                status,
                request.Reason,
                detailCode),
            cancellationToken);

    private static void Validate(ControlPlaneActor actor, ControlPlaneOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.ActorId);
        ArgumentNullException.ThrowIfNull(actor.Roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Target);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Confirmation);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);
        if (request.OperationId == Guid.Empty)
        {
            throw new ArgumentException("The operation ID cannot be empty.", nameof(request));
        }

        if (!Enum.IsDefined(request.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        ThrowIfTooLong(actor.ActorId, 512, nameof(actor));
        ThrowIfTooLong(request.Target, 1024, nameof(request));
        ThrowIfTooLong(request.Confirmation, 2048, nameof(request));
        ThrowIfTooLong(request.Reason, 2048, nameof(request));
    }

    private static void ThrowIfTooLong(string value, int maximum, string parameterName)
    {
        if (value.Length > maximum)
        {
            throw new ArgumentException(
                $"The value cannot exceed {maximum} characters.",
                parameterName);
        }
    }
}

public class ControlPlaneOperationException : Exception
{
    public ControlPlaneOperationException(string message)
        : base(message)
    {
    }

    public ControlPlaneOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ControlPlaneAuthorizationException : ControlPlaneOperationException
{
    public ControlPlaneAuthorizationException(string message)
        : base(message)
    {
    }
}

public sealed class ControlPlaneConfirmationException : ControlPlaneOperationException
{
    public ControlPlaneConfirmationException(string message)
        : base(message)
    {
    }
}

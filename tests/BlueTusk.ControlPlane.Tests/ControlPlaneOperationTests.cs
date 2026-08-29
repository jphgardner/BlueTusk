namespace BlueTusk.ControlPlane.Tests;

public sealed class ControlPlaneOperationTests
{
    [Fact]
    public void Deployment_delete_requires_administrator_and_exact_confirmation()
    {
        var policy = ControlPlaneOperationPolicies.Get(
            ControlPlaneOperationKind.DeleteDeployment,
            "deployment:production/orders");

        Assert.Equal(ControlPlaneRole.Administrator, policy.RequiredRole);
        Assert.True(policy.IsDestructive);
        Assert.Equal(
            "DeleteDeployment:deployment:production/orders",
            policy.RequiredConfirmation);
    }

    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 3, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Operator_executes_confirmed_operation_with_request_and_success_audits()
    {
        var audit = new RecordingAuditStore();
        var handler = new RecordingHandler();
        var executor = new ControlPlaneOperationExecutor(
            new RoleControlPlaneAuthorizer(),
            audit,
            handler,
            new FixedTimeProvider(Timestamp));
        var request = Request(ControlPlaneOperationKind.PauseSource, "source:orders");

        await executor.ExecuteAsync(
            Actor(ControlPlaneRole.Operator),
            request);

        Assert.Equal(1, handler.ExecutionCount);
        Assert.Collection(
            audit.Records,
            record => AssertAudit(record, request, ControlPlaneAuditStatus.Requested, null),
            record => AssertAudit(record, request, ControlPlaneAuditStatus.Succeeded, null));
    }

    [Fact]
    public async Task Confirmation_mismatch_is_audited_and_never_reaches_handler()
    {
        var audit = new RecordingAuditStore();
        var handler = new RecordingHandler();
        var executor = new ControlPlaneOperationExecutor(
            new RoleControlPlaneAuthorizer(),
            audit,
            handler,
            new FixedTimeProvider(Timestamp));
        var request = Request(ControlPlaneOperationKind.PauseSource, "source:orders") with
        {
            Confirmation = "PauseSource:source:other",
        };

        await Assert.ThrowsAsync<ControlPlaneConfirmationException>(
            () => executor.ExecuteAsync(Actor(ControlPlaneRole.Operator), request).AsTask());

        Assert.Equal(0, handler.ExecutionCount);
        AssertAudit(Assert.Single(audit.Records), request, ControlPlaneAuditStatus.Rejected, "confirmation-mismatch");
    }

    [Fact]
    public async Task Destructive_operation_requires_administrator_and_audits_denial()
    {
        var audit = new RecordingAuditStore();
        var handler = new RecordingHandler();
        var executor = new ControlPlaneOperationExecutor(
            new RoleControlPlaneAuthorizer(),
            audit,
            handler,
            new FixedTimeProvider(Timestamp));
        var request = Request(ControlPlaneOperationKind.DeleteSlot, "slot:orders");

        await Assert.ThrowsAsync<ControlPlaneAuthorizationException>(
            () => executor.ExecuteAsync(Actor(ControlPlaneRole.Operator), request).AsTask());

        Assert.Equal(0, handler.ExecutionCount);
        AssertAudit(Assert.Single(audit.Records), request, ControlPlaneAuditStatus.Denied, "role-denied");
        Assert.True(ControlPlaneOperationPolicies.Get(request.Kind, request.Target).IsDestructive);
    }

    [Fact]
    public async Task Handler_failure_is_audited_without_exposing_exception_message()
    {
        var audit = new RecordingAuditStore();
        var handler = new RecordingHandler { Exception = new InvalidOperationException("sensitive") };
        var executor = new ControlPlaneOperationExecutor(
            new RoleControlPlaneAuthorizer(),
            audit,
            handler,
            new FixedTimeProvider(Timestamp));
        var request = Request(ControlPlaneOperationKind.ReconcilePipeline, "pipeline:search");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(Actor(ControlPlaneRole.Operator), request).AsTask());

        Assert.Equal("sensitive", exception.Message);
        Assert.Collection(
            audit.Records,
            record => AssertAudit(record, request, ControlPlaneAuditStatus.Requested, null),
            record => AssertAudit(record, request, ControlPlaneAuditStatus.Failed, nameof(InvalidOperationException)));
        Assert.DoesNotContain(audit.Records, record => record.DetailCode == "sensitive");
    }

    [Fact]
    public async Task Undefined_role_value_fails_closed_and_is_audited()
    {
        var audit = new RecordingAuditStore();
        var handler = new RecordingHandler();
        var executor = new ControlPlaneOperationExecutor(
            new RoleControlPlaneAuthorizer(),
            audit,
            handler,
            new FixedTimeProvider(Timestamp));
        var request = Request(ControlPlaneOperationKind.PauseSource, "source:orders");

        await Assert.ThrowsAsync<ControlPlaneAuthorizationException>(
            () => executor.ExecuteAsync(Actor((ControlPlaneRole)999), request).AsTask());

        Assert.Equal(0, handler.ExecutionCount);
        Assert.Equal(ControlPlaneAuditStatus.Denied, Assert.Single(audit.Records).Status);
    }

    [Fact]
    public async Task Success_audit_failure_reports_reconciliation_without_false_failed_record()
    {
        var audit = new RecordingAuditStore { FailureOnAppend = 2 };
        var handler = new RecordingHandler();
        var executor = new ControlPlaneOperationExecutor(
            new RoleControlPlaneAuthorizer(),
            audit,
            handler,
            new FixedTimeProvider(Timestamp));
        var request = Request(ControlPlaneOperationKind.PauseSource, "source:orders");

        var exception = await Assert.ThrowsAsync<ControlPlaneOperationException>(
            () => executor.ExecuteAsync(Actor(ControlPlaneRole.Operator), request).AsTask());

        Assert.Contains("reconcile", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.ExecutionCount);
        Assert.Equal(ControlPlaneAuditStatus.Requested, Assert.Single(audit.Records).Status);
    }

    private static ControlPlaneActor Actor(ControlPlaneRole role) =>
        new("operator@example.invalid", new HashSet<ControlPlaneRole> { role });

    private static ControlPlaneOperationRequest Request(
        ControlPlaneOperationKind kind,
        string target) =>
        new(
            Guid.NewGuid(),
            kind,
            target,
            ControlPlaneOperationPolicies.Get(kind, target).RequiredConfirmation,
            "Acceptance test");

    private static void AssertAudit(
        ControlPlaneAuditRecord record,
        ControlPlaneOperationRequest request,
        ControlPlaneAuditStatus status,
        string? detail)
    {
        Assert.Equal(request.OperationId, record.OperationId);
        Assert.Equal(Timestamp, record.OccurredAt);
        Assert.Equal(request.Kind, record.Kind);
        Assert.Equal(request.Target, record.Target);
        Assert.Equal(status, record.Status);
        Assert.Equal(detail, record.DetailCode);
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class RecordingAuditStore : IControlPlaneAuditStore
    {
        private int _appendCount;

        public List<ControlPlaneAuditRecord> Records { get; } = [];

        public int FailureOnAppend { get; init; }

        public ValueTask AppendAsync(
            ControlPlaneAuditRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _appendCount++;
            if (_appendCount == FailureOnAppend)
            {
                throw new InvalidOperationException("Injected audit failure.");
            }

            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingHandler : IControlPlaneOperationHandler
    {
        public int ExecutionCount { get; private set; }

        public Exception? Exception { get; init; }

        public ValueTask ExecuteAsync(
            ControlPlaneOperationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            if (Exception is not null)
            {
                throw Exception;
            }

            return ValueTask.CompletedTask;
        }
    }
}

using BlueTusk.ControlPlane;
using BlueTusk.Live;

namespace BlueTusk.ContinuousGraph.Tests;

public sealed class ContinuousGraphControlPlaneExecutionTests
{
    private static readonly string Fingerprint = new('e', 64);

    [Fact]
    public async Task Registered_execution_preserves_server_bound_security_and_returns_complete_graph()
    {
        LiveSecurityScope? observedScope = null;
        LiveQueryArguments? observedArguments = null;
        var plan = CreatePlan((context, _) =>
        {
            observedScope = context.SecurityScope;
            observedArguments = context.Arguments;
            IReadOnlyList<GraphRow> rows =
            [new(1, 2, "Account 1", "Account 2", 125m),
             new(1, 3, "Account 1", "Account 3", 275m)];
            return ValueTask.FromResult(rows);
        });
        var metadata = new ContinuousGraphQueryRegistry();
        Assert.True(metadata.Register(plan));
        var executions = new ContinuousGraphControlPlaneExecutionRegistry();
        Assert.True(executions.Register(
            plan,
            new Dictionary<string, object?>
            {
                ["minimumRisk"] = 0.70m,
                ["tenantId"] = "tenant-secret-value",
            },
            ["minimumRisk"],
            actor => new LiveSecurityScope("actor:" + actor.ActorId, "policy-v3"),
            Project));
        var queryService = new ExecutableContinuousGraphControlPlaneQueryService(
            metadata,
            executions,
            new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)));
        using var executionService = new HostedContinuousGraphControlPlaneExecutionService(
            executions,
            new ContinuousGraphControlPlaneExecutionOptions
            {
                ExecutionTimeout = TimeSpan.FromSeconds(5),
                MaximumConcurrentExecutions = 2,
                MaximumNodes = 10,
                MaximumEdges = 10,
            });

        var overview = await queryService.GetContinuousGraphOverviewAsync(
            TestContext.Current.CancellationToken);
        var query = Assert.Single(overview.Queries);
        Assert.True(query.CanExecute);
        Assert.Equal(2, query.Parameters.Count);
        Assert.Equal("0.70", query.Parameters.Single(parameter => parameter.Editable).SuggestedValue);
        Assert.Null(query.Parameters.Single(parameter => !parameter.Editable).SuggestedValue);

        var result = await executionService.ExecuteAsync(
            Fingerprint,
            new ControlPlaneActor("operator-a", new HashSet<ControlPlaneRole>
            {
                ControlPlaneRole.Operator,
            }),
            new ControlPlaneContinuousGraphRunRequest(
                new Dictionary<string, string?> { ["minimumRisk"] = "0.85" }),
            TestContext.Current.CancellationToken);

        Assert.Equal("actor:operator-a", observedScope?.Scope);
        Assert.Equal("policy-v3", observedScope?.AuthorizationPolicyVersion);
        Assert.Equal(0.85m, observedArguments?.Get<decimal>("minimumRisk"));
        Assert.Equal("tenant-secret-value", observedArguments?.Get<string>("tenantId"));
        Assert.Equal(2, result.ResultRowCount);
        Assert.Equal(3, result.Nodes.Count);
        Assert.Equal(2, result.Edges.Count);
        Assert.Equal([new ControlPlaneContinuousGraphComposition("Account", 3)], result.NodeComposition);
        Assert.Equal([new ControlPlaneContinuousGraphComposition("Transfer", 2)], result.EdgeComposition);
    }

    [Fact]
    public async Task Execution_rejects_non_editable_parameters_and_incomplete_graphs()
    {
        var plan = CreatePlan((_, _) => ValueTask.FromResult<IReadOnlyList<GraphRow>>(
            [new(1, 2, "Account 1", "Account 2", 125m)]));
        var executions = new ContinuousGraphControlPlaneExecutionRegistry();
        Assert.True(executions.Register(
            plan,
            new Dictionary<string, object?>
            {
                ["minimumRisk"] = 0.70m,
                ["tenantId"] = "tenant-a",
            },
            ["minimumRisk"],
            _ => new LiveSecurityScope("tenant:a", "policy-v1"),
            row => new ControlPlaneContinuousGraphFragment(
                [Node(row.SourceId, row.SourceName)],
                [new ControlPlaneContinuousGraphEdge(
                    "transfer:1",
                    "account:1",
                    "account:missing",
                    "TRANSFERRED_TO",
                    "Transfer",
                    true,
                    [])])));
        using var service = new HostedContinuousGraphControlPlaneExecutionService(executions);
        var actor = new ControlPlaneActor(
            "operator-a",
            new HashSet<ControlPlaneRole> { ControlPlaneRole.Operator });

        var parameterException = await Assert.ThrowsAsync<
            ControlPlaneContinuousGraphExecutionException>(() => service.ExecuteAsync(
                Fingerprint,
                actor,
                new ControlPlaneContinuousGraphRunRequest(
                    new Dictionary<string, string?> { ["tenantId"] = "attacker-value" }),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("graph-parameter-not-editable", parameterException.Code);

        var projectionException = await Assert.ThrowsAsync<
            ControlPlaneContinuousGraphExecutionException>(() => service.ExecuteAsync(
                Fingerprint,
                actor,
                new ControlPlaneContinuousGraphRunRequest(
                    new Dictionary<string, string?>()),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("graph-result-invalid", projectionException.Code);
    }

    private static ContinuousGraphQueryPlan<GraphRow, int> CreatePlan(
        Func<LiveQueryExecutionContext, CancellationToken, ValueTask<IReadOnlyList<GraphRow>>>
            executeAsync)
    {
        var livePlan = new LiveQueryPlan<GraphRow, int>(
            "risk-query",
            "risk-primary",
            Fingerprint,
            LiveQueryCapabilities.TenantFilter |
            LiveQueryCapabilities.DeterministicOrdering |
            LiveQueryCapabilities.BoundedTake,
            [new LiveTableDependency("risk", "accounts"),
             new LiveTableDependency("risk", "transfers")],
            [new LiveQueryParameter("minimumRisk", typeof(decimal)),
             new LiveQueryParameter("tenantId", typeof(string))],
            100,
            executeAsync,
            static row => row.TargetId);
        return new ContinuousGraphQueryPlan<GraphRow, int>(
            "fraud_network",
            "risk",
            ["account", "transfer"],
            livePlan);
    }

    private static ControlPlaneContinuousGraphFragment Project(GraphRow row) =>
        new(
            [Node(row.SourceId, row.SourceName), Node(row.TargetId, row.TargetName)],
            [new ControlPlaneContinuousGraphEdge(
                $"transfer:{row.SourceId}:{row.TargetId}",
                $"account:{row.SourceId}",
                $"account:{row.TargetId}",
                "TRANSFERRED_TO",
                "Transfer",
                true,
                [new ControlPlaneContinuousGraphProperty(
                    "amount",
                    row.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture))])]);

    private static ControlPlaneContinuousGraphNode Node(int id, string name) =>
        new(
            $"account:{id}",
            name,
            "Account",
            [new ControlPlaneContinuousGraphProperty("id", id.ToString(System.Globalization.CultureInfo.InvariantCulture))]);

    private sealed record GraphRow(
        int SourceId,
        int TargetId,
        string SourceName,
        string TargetName,
        decimal Amount);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

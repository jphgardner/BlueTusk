using BlueTusk.Live;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.ContinuousGraph.Tests;

public sealed class ContinuousGraphAutomaticMaintenanceTests
{
    private static readonly ChangeSourceIdentity Source =
        new("graph-system", "graph-database", "slot", "publication");
    private static readonly LiveQueryExecutionContext Execution =
        new(
            LiveQueryArguments.Create([], new Dictionary<string, object?>()),
            new LiveSecurityScope("tenant:alpha", "policy-v1"));

    [Fact]
    public async Task Edge_endpoint_keys_drive_one_authoritative_scoped_query()
    {
        var observed = Array.Empty<int>();
        var evaluator = CreateEvaluator(
            async (keys, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                observed = keys.ToArray();
                await Task.Yield();
                return [new GraphRow(keys.Single(), 100)];
            });
        await using var delivery = InsertEdge(7, 10, sourceId: 4, targetId: 42);

        var result = await evaluator.EvaluateAsync(
            delivery.Transaction,
            Execution,
            TestContext.Current.CancellationToken);

        Assert.Equal(ContinuousGraphIncrementalDisposition.Exact, result.Disposition);
        Assert.Equal(ContinuousGraphMaintenanceTier.AuthoritativeDelta, result.MaintenanceTier);
        Assert.Equal([42], observed);
        Assert.Equal(42, Assert.Single(result.Rows).Id);
    }

    [Fact]
    public async Task Incomplete_endpoint_tuple_fails_closed_to_repair()
    {
        var evaluator = CreateEvaluator(
            static (_, _, _) => throw new InvalidOperationException(
                "A scoped query must not execute for an unprovable tuple."));
        await using var delivery = DeleteEdgeWithUnavailableTarget(8, 11);

        var result = await evaluator.EvaluateAsync(
            delivery.Transaction,
            Execution,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ContinuousGraphIncrementalDisposition.RequiresRepair,
            result.Disposition);
        Assert.Equal("incomplete-or-undecodable-key-tuple", result.Detail);
    }

    [Fact]
    public async Task Matching_complete_trust_contract_uses_direct_cdc_tier()
    {
        var scopedQueries = 0;
        var projector = new Projector(
            "impact-fingerprint",
            ContinuousGraphIncrementalResult<GraphRow, int>.Exact(
                [42],
                [new GraphRow(42, 101)],
                "trusted-projection"));
        var evaluator = CreateEvaluator(
            (keys, _, _) =>
            {
                scopedQueries++;
                return ValueTask.FromResult<IReadOnlyList<GraphRow>>(
                    [new GraphRow(keys.Single(), 100)]);
            },
            projector);
        await using var delivery = InsertEdge(9, 12, sourceId: 4, targetId: 42);

        var result = await evaluator.EvaluateAsync(
            delivery.Transaction,
            Execution,
            TestContext.Current.CancellationToken);

        Assert.Equal(ContinuousGraphMaintenanceTier.TrustedCdcDelta, result.MaintenanceTier);
        Assert.Equal(0, scopedQueries);
        Assert.Equal("trusted-projection", result.Detail);
    }

    [Fact]
    public async Task Mismatched_trust_contract_is_not_invoked()
    {
        var projector = new Projector(
            "different-schema",
            ContinuousGraphIncrementalResult<GraphRow, int>.Exact(
                [42],
                [new GraphRow(42, 101)]));
        var evaluator = CreateEvaluator(
            (keys, _, _) => ValueTask.FromResult<IReadOnlyList<GraphRow>>(
                [new GraphRow(keys.Single(), 100)]),
            projector);
        await using var delivery = InsertEdge(10, 13, sourceId: 4, targetId: 42);

        var result = await evaluator.EvaluateAsync(
            delivery.Transaction,
            Execution,
            TestContext.Current.CancellationToken);

        Assert.Equal(ContinuousGraphMaintenanceTier.AuthoritativeDelta, result.MaintenanceTier);
        Assert.Equal(0, projector.Executions);
    }

    [Fact]
    public async Task Relevant_truncate_forces_authoritative_repair()
    {
        var evaluator = CreateEvaluator(
            static (_, _, _) => throw new InvalidOperationException(
                "A truncate must never execute an affected-key query."));
        var id = Identity(11, 14);
        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            11,
            new BlueTuskLogSequenceNumber(14),
            [new TruncateChange(id, [EdgeTable()], Cascade: false, RestartIdentity: false)]);

        var result = await evaluator.EvaluateAsync(
            delivery.Transaction,
            Execution,
            TestContext.Current.CancellationToken);

        Assert.Equal(ContinuousGraphIncrementalDisposition.RequiresRepair, result.Disposition);
        Assert.Equal("relevant-truncate", result.Detail);
    }

    [Fact]
    public async Task Unrelated_table_does_not_query_or_repair()
    {
        var evaluator = CreateEvaluator(
            static (_, _, _) => throw new InvalidOperationException(
                "An unrelated table must not execute an affected-key query."));
        var table = new ChangeTable(
            2,
            "audit",
            "events",
            'f',
            [new ChangeColumn(0, "id", 23, -1, IsKey: true)]);
        var id = Identity(12, 15);
        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            12,
            new BlueTuskLogSequenceNumber(15),
            [new InsertChange(id, new ChangeRow(table, [Text(99)]))]);

        var result = await evaluator.EvaluateAsync(
            delivery.Transaction,
            Execution,
            TestContext.Current.CancellationToken);

        Assert.Equal(ContinuousGraphIncrementalDisposition.Unrelated, result.Disposition);
        Assert.Equal("no-graph-table-dependency", result.Detail);
    }

    [Fact]
    public async Task Authoritative_query_receives_the_original_security_scope()
    {
        LiveQueryExecutionContext? observed = null;
        var evaluator = CreateEvaluator(
            (keys, context, _) =>
            {
                observed = context;
                return ValueTask.FromResult<IReadOnlyList<GraphRow>>(
                    [new GraphRow(keys.Single(), 100)]);
            });
        await using var delivery = InsertEdge(13, 16, sourceId: 4, targetId: 42);

        await evaluator.EvaluateAsync(
            delivery.Transaction,
            Execution,
            TestContext.Current.CancellationToken);

        Assert.NotNull(observed);
        Assert.Same(Execution.SecurityScope, observed.SecurityScope);
        Assert.Same(Execution.Arguments, observed.Arguments);
    }

    [Fact]
    public async Task Affected_key_overflow_fails_closed_without_querying()
    {
        var evaluator = new ContinuousGraphTieredEvaluator<GraphRow, int>(
            "impact-fingerprint",
            [new ContinuousGraphAutomaticTableImpact(
                "graph",
                "edges",
                ["source_id", "target_id"],
                IsComplete: true)],
            static (_, _, _) => throw new InvalidOperationException(
                "An overflowed delta must not execute an affected-key query."),
            maximumAffectedKeys: 1,
            EqualityComparer<int>.Default,
            trustedProjector: null);
        await using var delivery = InsertEdge(14, 17, sourceId: 4, targetId: 42);

        var result = await evaluator.EvaluateAsync(
            delivery.Transaction,
            Execution,
            TestContext.Current.CancellationToken);

        Assert.Equal(ContinuousGraphIncrementalDisposition.RequiresRepair, result.Disposition);
        Assert.Equal("affected-key-limit:2", result.Detail);
    }

    private static ContinuousGraphTieredEvaluator<GraphRow, int> CreateEvaluator(
        Func<IReadOnlyCollection<int>, LiveQueryExecutionContext, CancellationToken,
            ValueTask<IReadOnlyList<GraphRow>>> execute,
        IContinuousGraphCdcProjector<GraphRow, int>? projector = null) =>
        new(
            "impact-fingerprint",
            [new ContinuousGraphAutomaticTableImpact(
                "graph",
                "edges",
                ["target_id"],
                IsComplete: true)],
            execute,
            maximumAffectedKeys: 16,
            EqualityComparer<int>.Default,
            projector);

    private static ChangeTransactionDelivery InsertEdge(
        uint transactionId,
        ulong position,
        int sourceId,
        int targetId)
    {
        var table = EdgeTable();
        var id = Identity(transactionId, position);
        var row = new ChangeRow(
            table,
            [Text(sourceId), Text(targetId)]);
        return ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            transactionId,
            new BlueTuskLogSequenceNumber(position),
            [new InsertChange(id, row)]);
    }

    private static ChangeTransactionDelivery DeleteEdgeWithUnavailableTarget(
        uint transactionId,
        ulong position)
    {
        var table = EdgeTable();
        var id = Identity(transactionId, position);
        var row = new ChangeRow(
            table,
            [Text(4), ChangeColumnValue.OldValueUnavailable]);
        return ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            transactionId,
            new BlueTuskLogSequenceNumber(position),
            [new DeleteChange(id, row)]);
    }

    private static ChangeTable EdgeTable() =>
        new(
            1,
            "graph",
            "edges",
            'f',
            [
                new ChangeColumn(0, "source_id", 23, -1, IsKey: true),
                new ChangeColumn(1, "target_id", 23, -1, IsKey: true),
            ]);

    private static ChangeId Identity(uint transactionId, ulong position) =>
        new(
            Source,
            new BlueTuskLogSequenceNumber(position),
            transactionId,
            0);

    private static ChangeColumnValue Text(int value) =>
        ChangeColumnValue.FromValue(
            System.Text.Encoding.UTF8.GetBytes(value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            ChangeValueEncoding.Text);

    private sealed record GraphRow(int Id, int Score);

    private sealed class Projector(
        string fingerprint,
        ContinuousGraphIncrementalResult<GraphRow, int> result) :
        IContinuousGraphCdcProjector<GraphRow, int>
    {
        public ContinuousGraphCdcTrustContract TrustContract { get; } =
            new(
                fingerprint,
                HasCompleteOldAndNewValues: true,
                HasExactChangedColumns: true,
                HasSufficientReplicaIdentity: true,
                EnforcesSecurityScope: true);

        public int Executions { get; private set; }

        public ValueTask<ContinuousGraphIncrementalResult<GraphRow, int>> ProjectAsync(
            ChangeTransaction transaction,
            LiveQueryExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Executions++;
            return ValueTask.FromResult(result);
        }
    }
}

using BlueTusk.Live;
using BlueTusk.Live.Testing;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.ContinuousGraph.Tests;

public sealed class ContinuousGraphIncrementalTests
{
    private static readonly ChangeSourceIdentity Source =
        new("graph-system", "graph-database", "graph-slot", "graph-publication");

    [Fact]
    public async Task Exact_rows_update_and_admit_top_n_candidates_without_full_query()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var authoritative = new List<GraphRow>
        {
            new(1, 100, "one"),
            new(2, 90, "two"),
        };
        var evaluator = new QueueEvaluator();
        await using var session = CreateSession(authoritative, evaluator);
        await CommitInitialAsync(session, cancellationToken);
        Assert.Equal(1, evaluator.AuthoritativeExecutions);

        evaluator.Enqueue(ContinuousGraphIncrementalResult<GraphRow, int>.Exact(
            [1],
            [new GraphRow(1, 110, "ONE")]));
        await using (var source = Delivery(10, 10))
        await using (var evaluation =
            await session.PrepareTransactionAsync(
                source.Transaction,
                cancellationToken))
        {
            Assert.Equal(
                ContinuousGraphEvaluationMode.Incremental,
                evaluation.Evaluation.Mode);
            Assert.Contains(
                evaluation.Evaluation.Batch!.Events,
                graphEvent =>
                    graphEvent.Kind is LiveEventKind.RowUpdated &&
                    graphEvent.Key == 1);
            await evaluation.CommitAsync(cancellationToken);
        }

        evaluator.Enqueue(ContinuousGraphIncrementalResult<GraphRow, int>.Exact(
            [3],
            [new GraphRow(3, 105, "three")]));
        await using (var source = Delivery(11, 11))
        await using (var evaluation =
            await session.PrepareTransactionAsync(
                source.Transaction,
                cancellationToken))
        {
            Assert.Equal(
                ContinuousGraphEvaluationMode.Incremental,
                evaluation.Evaluation.Mode);
            Assert.Equal(
                [1, 3],
                evaluation.Evaluation.Batch!.Snapshot.Keys);
            Assert.Contains(
                evaluation.Evaluation.Batch.Events,
                graphEvent =>
                    graphEvent.Kind is LiveEventKind.RowRemoved &&
                    graphEvent.Key == 2);
            Assert.Contains(
                evaluation.Evaluation.Batch.Events,
                graphEvent =>
                    graphEvent.Kind is LiveEventKind.RowAdded &&
                    graphEvent.Key == 3);
            await evaluation.CommitAsync(cancellationToken);
        }

        Assert.Equal(1, evaluator.AuthoritativeExecutions);
        Assert.Equal(2, session.Status.IncrementalTransactions);
        Assert.Equal(0, session.Status.FallbackRepairs);
    }

    [Fact]
    public async Task Visible_removal_falls_back_to_authoritative_query()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var authoritative = new List<GraphRow>
        {
            new(1, 100, "one"),
            new(2, 90, "two"),
        };
        var evaluator = new QueueEvaluator();
        await using var session = CreateSession(authoritative, evaluator);
        await CommitInitialAsync(session, cancellationToken);
        authoritative.RemoveAll(static row => row.Id == 1);
        evaluator.Enqueue(ContinuousGraphIncrementalResult<GraphRow, int>.Exact(
            [1],
            []));

        await using var source = Delivery(10, 10);
        await using var evaluation =
            await session.PrepareTransactionAsync(
                source.Transaction,
                cancellationToken);

        Assert.Equal(
            ContinuousGraphEvaluationMode.AuthoritativeRepair,
            evaluation.Evaluation.Mode);
        Assert.Equal(
            "visible-row-removed-or-left-predicate",
            evaluation.Evaluation.Detail);
        Assert.Equal([2], evaluation.Evaluation.Batch!.Snapshot.Keys);
        await evaluation.CommitAsync(cancellationToken);
        Assert.Equal(2, evaluator.AuthoritativeExecutions);
        Assert.Equal(1, session.Status.FallbackRepairs);
    }

    [Fact]
    public async Task Worsening_visible_rank_falls_back_to_recover_hidden_candidates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var authoritative = new List<GraphRow>
        {
            new(1, 100, "one"),
            new(2, 90, "two"),
            new(3, 85, "three"),
        };
        var evaluator = new QueueEvaluator();
        await using var session = CreateSession(authoritative, evaluator);
        await CommitInitialAsync(session, cancellationToken);
        authoritative[0] = new GraphRow(1, 80, "one");
        evaluator.Enqueue(ContinuousGraphIncrementalResult<GraphRow, int>.Exact(
            [1],
            [authoritative[0]]));

        await using var source = Delivery(10, 10);
        await using var evaluation =
            await session.PrepareTransactionAsync(
                source.Transaction,
                cancellationToken);

        Assert.Equal(
            ContinuousGraphEvaluationMode.AuthoritativeRepair,
            evaluation.Evaluation.Mode);
        Assert.Equal("visible-row-rank-worsened", evaluation.Evaluation.Detail);
        Assert.Equal([2, 3], evaluation.Evaluation.Batch!.Snapshot.Keys);
        await evaluation.CommitAsync(cancellationToken);
    }

    [Fact]
    public async Task Affected_key_budget_and_periodic_repair_fail_closed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var authoritative = new List<GraphRow>
        {
            new(1, 100, "one"),
            new(2, 90, "two"),
        };
        var evaluator = new QueueEvaluator();
        var options = new ContinuousGraphIncrementalOptions<GraphRow, int>
        {
            ResultOrdering = CreateResultOrdering(),
            KeyOrdering = Comparer<int>.Default,
            MaximumAffectedKeys = 1,
            RepairAfterTransactions = 1,
            MaximumRepairInterval = TimeSpan.FromHours(1),
        };
        await using var session = CreateSession(authoritative, evaluator, options);
        await CommitInitialAsync(session, cancellationToken);
        evaluator.Enqueue(ContinuousGraphIncrementalResult<GraphRow, int>.Exact(
            [1, 2],
            authoritative));

        await using (var source = Delivery(10, 10))
        await using (var evaluation =
            await session.PrepareTransactionAsync(
                source.Transaction,
                cancellationToken))
        {
            Assert.Equal(
                ContinuousGraphEvaluationMode.AuthoritativeRepair,
                evaluation.Evaluation.Mode);
            Assert.Equal("affected-key-limit:2", evaluation.Evaluation.Detail);
            await evaluation.CommitAsync(cancellationToken);
        }

        evaluator.Enqueue(
            ContinuousGraphIncrementalResult<GraphRow, int>.Unrelated());
        await using (var source = Delivery(11, 11))
        await using (var evaluation =
            await session.PrepareTransactionAsync(
                source.Transaction,
                cancellationToken))
        {
            Assert.Equal(
                ContinuousGraphEvaluationMode.Unrelated,
                evaluation.Evaluation.Mode);
            await evaluation.CommitAsync(cancellationToken);
        }

        await using (var source = Delivery(12, 12))
        await using (var evaluation =
            await session.PrepareTransactionAsync(
                source.Transaction,
                cancellationToken))
        {
            Assert.Equal(
                ContinuousGraphEvaluationMode.AuthoritativeRepair,
                evaluation.Evaluation.Mode);
            Assert.Equal(
                "scheduled-authoritative-repair",
                evaluation.Evaluation.Detail);
            await evaluation.CommitAsync(cancellationToken);
        }

        Assert.Equal(3, evaluator.AuthoritativeExecutions);
        Assert.Equal(2, evaluator.IncrementalExecutions);
    }

    [Fact]
    public async Task Uncommitted_proposal_rolls_back_and_duplicate_is_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var authoritative = new List<GraphRow>
        {
            new(1, 100, "one"),
        };
        var evaluator = new QueueEvaluator();
        await using var session = CreateSession(authoritative, evaluator);
        await CommitInitialAsync(session, cancellationToken);
        evaluator.Enqueue(ContinuousGraphIncrementalResult<GraphRow, int>.Exact(
            [1],
            [new GraphRow(1, 110, "ONE")]));

        await using var source = Delivery(10, 10);
        await using (var abandoned =
            await session.PrepareTransactionAsync(
                source.Transaction,
                cancellationToken))
        {
            Assert.Equal(
                ContinuousGraphEvaluationMode.Incremental,
                abandoned.Evaluation.Mode);
        }

        Assert.Null(session.Status.SourcePosition);
        evaluator.Enqueue(ContinuousGraphIncrementalResult<GraphRow, int>.Exact(
            [1],
            [new GraphRow(1, 110, "ONE")]));
        await using (var committed =
            await session.PrepareTransactionAsync(
                source.Transaction,
                cancellationToken))
        {
            await committed.CommitAsync(cancellationToken);
        }

        await using (var duplicate =
            await session.PrepareTransactionAsync(
                source.Transaction,
                cancellationToken))
        {
            Assert.Equal(
                ContinuousGraphEvaluationMode.Duplicate,
                duplicate.Evaluation.Mode);
            Assert.Null(duplicate.Evaluation.Batch);
            await duplicate.CommitAsync(cancellationToken);
        }

        await using var older = Delivery(9, 9);
        await Assert.ThrowsAsync<ContinuousGraphIncrementalException>(
            () => session.PrepareTransactionAsync(
                older.Transaction,
                cancellationToken).AsTask());
        Assert.Equal(2, evaluator.IncrementalExecutions);
        Assert.Equal(1, session.Status.IncrementalTransactions);
        Assert.Equal(1, session.Status.DuplicateTransactions);
    }

    [Fact]
    public async Task Evaluator_contract_rejects_rows_outside_the_affected_key_set()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var authoritative = new List<GraphRow>
        {
            new(1, 100, "one"),
        };
        var evaluator = new QueueEvaluator();
        await using var session = CreateSession(authoritative, evaluator);
        await CommitInitialAsync(session, cancellationToken);
        evaluator.Enqueue(ContinuousGraphIncrementalResult<GraphRow, int>.Exact(
            [1],
            [new GraphRow(2, 100, "wrong")]));
        await using var source = Delivery(10, 10);

        var exception = await Assert.ThrowsAsync<ContinuousGraphIncrementalException>(
            () => session.PrepareTransactionAsync(
                source.Transaction,
                cancellationToken).AsTask());

        Assert.Contains("outside its affected-key set", exception.Message, StringComparison.Ordinal);
        Assert.Null(session.Status.SourcePosition);
    }

    [Fact]
    public async Task Two_phase_lifecycle_repairs_only_on_commit_prepared()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var authoritative = new List<GraphRow>
        {
            new(1, 100, "one"),
        };
        var evaluator = new QueueEvaluator();
        await using var session = CreateSession(authoritative, evaluator);
        await CommitInitialAsync(session, cancellationToken);

        await using (var prepared = TwoPhaseDelivery(
            10,
            10,
            ChangeTransactionOutcome.Prepared,
            "prepared-a"))
        await using (var evaluation =
            await session.PrepareTransactionAsync(
                prepared.Transaction,
                cancellationToken))
        {
            Assert.Equal(
                ContinuousGraphEvaluationMode.Unrelated,
                evaluation.Evaluation.Mode);
            await evaluation.CommitAsync(cancellationToken);
        }

        authoritative[0] = new GraphRow(1, 110, "ONE");
        await using (var committed = TwoPhaseDelivery(
            10,
            11,
            ChangeTransactionOutcome.Committed,
            "prepared-a"))
        await using (var evaluation =
            await session.PrepareTransactionAsync(
                committed.Transaction,
                cancellationToken))
        {
            Assert.Equal(
                ContinuousGraphEvaluationMode.AuthoritativeRepair,
                evaluation.Evaluation.Mode);
            Assert.Equal(
                "two-phase-commit-repair",
                evaluation.Evaluation.Detail);
            await evaluation.CommitAsync(cancellationToken);
        }

        await using (var rolledBack = TwoPhaseDelivery(
            11,
            12,
            ChangeTransactionOutcome.RolledBack,
            "prepared-b"))
        await using (var evaluation =
            await session.PrepareTransactionAsync(
                rolledBack.Transaction,
                cancellationToken))
        {
            Assert.Equal(
                ContinuousGraphEvaluationMode.Unrelated,
                evaluation.Evaluation.Mode);
            await evaluation.CommitAsync(cancellationToken);
        }

        Assert.Equal(0, evaluator.IncrementalExecutions);
        Assert.Equal(2, evaluator.AuthoritativeExecutions);
        Assert.Equal(1, session.Status.AuthoritativeRepairs);
        Assert.Equal(1, session.Status.FallbackRepairs);
        Assert.Equal(2, session.Status.UnrelatedTransactions);
    }

    [Fact]
    public async Task Consumer_persists_replay_before_ack_and_recovers_post_store_failure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var authoritative = new List<GraphRow>
        {
            new(1, 100, "one"),
        };
        var evaluator = new QueueEvaluator
        {
            FallbackResult = ContinuousGraphIncrementalResult<GraphRow, int>.Exact(
                [1],
                [new GraphRow(1, 110, "ONE")]),
        };
        await using var session = CreateSession(authoritative, evaluator);
        var timeline = new List<string>();
        var replay = new RecordingReplayStore(timeline);
        await using var consumer =
            new ContinuousGraphIncrementalConsumer<GraphRow, int>(session, replay);
        await consumer.InitializeAsync(cancellationToken);
        timeline.Clear();
        replay.ThrowAfterNextAppend = true;
        var firstObserver = new RecordingObserver(timeline);
        await using var first = Delivery(10, 10, firstObserver);

        await Assert.ThrowsAsync<IOException>(
            () => consumer.ConsumeTransactionAsync(
                first,
                cancellationToken).AsTask());

        Assert.Equal(ChangeDeliveryState.Nacked, first.State);
        Assert.Equal(["replay", "nack"], timeline);
        var retryObserver = new RecordingObserver(timeline);
        await using var retry = Delivery(10, 10, retryObserver);
        await consumer.ConsumeTransactionAsync(retry, cancellationToken);

        Assert.Equal(ChangeDeliveryState.Acknowledged, retry.State);
        Assert.Equal(["replay", "nack", "replay", "ack"], timeline);
        var stored = await replay.ReadAsync(
            consumer.Identity,
            0,
            10,
            cancellationToken);
        Assert.Equal(2, stored.LastSequence);
        Assert.Equal(2, stored.Events.Count);
        Assert.Equal(1, session.Status.IncrementalTransactions);
    }

    private static ContinuousGraphIncrementalSession<GraphRow, int> CreateSession(
        List<GraphRow> authoritative,
        QueueEvaluator evaluator,
        ContinuousGraphIncrementalOptions<GraphRow, int>? options = null)
    {
        var livePlan = new LiveQueryPlan<GraphRow, int>(
            "risk-network",
            "graph-database",
            new string('a', 64),
            LiveQueryCapabilities.TenantFilter |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake,
            [new LiveTableDependency("graph", "vertices")],
            [],
            3,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                evaluator.AuthoritativeExecutions++;
                IReadOnlyList<GraphRow> rows = authoritative
                    .Order(CreateOptions().ResultOrdering)
                    .Take(2)
                    .ToArray();
                return ValueTask.FromResult(rows);
            },
            static row => row.Id);
        var graphPlan = new ContinuousGraphQueryPlan<GraphRow, int>(
            "network",
            "graph",
            ["vertices"],
            livePlan);
        var arguments = graphPlan.Bind(
            new Dictionary<string, object?>());
        return graphPlan.CreateIncrementalSession(
            arguments,
            new LiveSecurityScope("tenant:alpha", "policy-v1"),
            evaluator,
            options ?? CreateOptions(),
            resultLimit: 2);
    }

    private static ContinuousGraphIncrementalOptions<GraphRow, int>
        CreateOptions() =>
        new()
        {
            ResultOrdering = CreateResultOrdering(),
            KeyOrdering = Comparer<int>.Default,
            MaximumRepairInterval = TimeSpan.FromHours(1),
        };

    private static Comparer<GraphRow> CreateResultOrdering() =>
        Comparer<GraphRow>.Create(
            static (left, right) => right.Score.CompareTo(left.Score));

    private static async ValueTask CommitInitialAsync(
        ContinuousGraphIncrementalSession<GraphRow, int> session,
        CancellationToken cancellationToken)
    {
        await using var initial =
            await session.PrepareInitialAsync(
                cancellationToken: cancellationToken);
        Assert.Equal(
            ContinuousGraphEvaluationMode.Initial,
            initial.Evaluation.Mode);
        await initial.CommitAsync(cancellationToken);
    }

    private static ChangeTransactionDelivery Delivery(
        uint transactionId,
        ulong position,
        IChangeDeliveryObserver? observer = null) =>
        ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            transactionId,
            new BlueTuskLogSequenceNumber(position),
            observer: observer);

    private static ChangeTransactionDelivery TwoPhaseDelivery(
        uint transactionId,
        ulong position,
        ChangeTransactionOutcome outcome,
        string globalTransactionId) =>
        ChangeDeliveryTestFactory.CreateTwoPhase(
            Source,
            transactionId,
            new BlueTuskLogSequenceNumber(position),
            outcome,
            globalTransactionId);

    private sealed record GraphRow(int Id, int Score, string Name);

    private sealed class QueueEvaluator :
        IContinuousGraphIncrementalEvaluator<GraphRow, int>
    {
        private readonly Queue<ContinuousGraphIncrementalResult<GraphRow, int>>
            _results = new();

        public ContinuousGraphIncrementalResult<GraphRow, int>? FallbackResult
        {
            get;
            init;
        }

        public int AuthoritativeExecutions { get; set; }

        public int IncrementalExecutions { get; private set; }

        public void Enqueue(
            ContinuousGraphIncrementalResult<GraphRow, int> result) =>
            _results.Enqueue(result);

        public ValueTask<ContinuousGraphIncrementalResult<GraphRow, int>>
            EvaluateAsync(
                ChangeTransaction transaction,
                LiveQueryExecutionContext context,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IncrementalExecutions++;
            if (_results.TryDequeue(out var result))
            {
                return ValueTask.FromResult(result);
            }

            return ValueTask.FromResult(
                FallbackResult ??
                ContinuousGraphIncrementalResult<GraphRow, int>.Unrelated());
        }
    }

    private sealed class RecordingObserver(List<string> timeline) :
        IChangeDeliveryObserver
    {
        public ValueTask AcknowledgeAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeline.Add("ack");
            return ValueTask.CompletedTask;
        }

        public ValueTask NackAsync(
            ChangeTransaction transaction,
            Exception? failure,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeline.Add("nack");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingReplayStore(List<string> timeline) :
        ILiveReplayStore
    {
        private readonly InMemoryLiveReplayStore _inner = new();

        public bool ThrowAfterNextAppend { get; set; }

        public async ValueTask<LiveReplayAppendResult> AppendAsync(
            LiveReplayAppendRequest request,
            CancellationToken cancellationToken = default)
        {
            timeline.Add("replay");
            var result = await _inner.AppendAsync(request, cancellationToken);
            if (ThrowAfterNextAppend)
            {
                ThrowAfterNextAppend = false;
                throw new IOException("Injected post-store failure.");
            }

            return result;
        }

        public ValueTask<LiveReplayReadResult> ReadAsync(
            LiveSubscriptionIdentity identity,
            long afterSequence,
            int maximumEvents,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(
                identity,
                afterSequence,
                maximumEvents,
                cancellationToken);

        public ValueTask<int> PruneAsync(
            CancellationToken cancellationToken = default) =>
            _inner.PruneAsync(cancellationToken);
    }
}

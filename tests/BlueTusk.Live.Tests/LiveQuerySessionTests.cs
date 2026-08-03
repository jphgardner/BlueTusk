namespace BlueTusk.Live.Tests;

public sealed class LiveQuerySessionTests
{
    [Fact]
    public async Task Initial_query_replays_concurrent_invalidation_before_becoming_live()
    {
        var log = new TestInvalidationLog();
        var rows = new List<Row> { new(1, "before") };
        var executions = 0;
        var plan = CreatePlan((_, _) =>
        {
            executions++;
            var result = rows.ToArray();
            if (executions == 1)
            {
                rows[0] = new Row(1, "after");
                log.Append(new LiveTableDependency("sales", "orders"));
            }

            return ValueTask.FromResult<IReadOnlyList<Row>>(result);
        });
        await using var session = CreateSession(plan, log);

        var initial = await session.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, executions);
        Assert.Equal("after", Assert.Single(initial.Snapshot.Rows).Value);
        Assert.Equal(1, initial.LastSequence);
        Assert.Equal(1, session.Status.Cursor.Value);
        Assert.Equal(1, session.Status.CoalescedInvalidationCount);
    }

    [Fact]
    public async Task Refresh_coalesces_transactions_and_requeries_only_for_dependencies()
    {
        var log = new TestInvalidationLog();
        IReadOnlyList<Row> rows = [new Row(1, "one")];
        var queryCount = 0;
        var plan = CreatePlan((_, _) =>
        {
            queryCount++;
            return ValueTask.FromResult(rows);
        });
        await using var session = CreateSession(plan, log);
        _ = await session.StartAsync(TestContext.Current.CancellationToken);

        log.Append(new LiveTableDependency("other", "table"));
        Assert.Null(await session.RefreshToCurrentAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, queryCount);

        rows = [new Row(1, "ONE"), new Row(2, "two")];
        log.Append(new LiveTableDependency("sales", "orders"));
        log.Append(new LiveTableDependency("sales", "orders"));
        var refresh = await session.RefreshToCurrentAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(refresh);
        Assert.Equal(2, queryCount);
        Assert.Collection(
            refresh.Events,
            updated => Assert.Equal(LiveEventKind.RowUpdated, updated.Kind),
            added => Assert.Equal(LiveEventKind.RowAdded, added.Kind));
        Assert.Equal(3, session.Status.Cursor.Value);
        Assert.Equal(1, session.Status.CoalescedInvalidationCount);
    }

    [Fact]
    public async Task Result_limit_and_cursor_regression_fail_closed()
    {
        var log = new TestInvalidationLog();
        var tooMany = Enumerable.Range(1, 3)
            .Select(id => new Row(id, id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        var plan = CreatePlan((_, _) => ValueTask.FromResult<IReadOnlyList<Row>>(tooMany), maximum: 2);
        await using var limited = CreateSession(plan, log);
        await Assert.ThrowsAsync<LiveQueryResultLimitException>(async () =>
            await limited.StartAsync(TestContext.Current.CancellationToken));

        IReadOnlyList<Row> rows = [new Row(1, "one")];
        var validPlan = CreatePlan((_, _) => ValueTask.FromResult(rows));
        await using var valid = CreateSession(validPlan, log);
        _ = await valid.StartAsync(TestContext.Current.CancellationToken);
        log.SetCursor(-1);
        await Assert.ThrowsAsync<LiveInvalidationCursorException>(async () =>
            await valid.RefreshToCurrentAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Perpetual_initial_churn_stops_at_configured_safe_boundary()
    {
        var log = new TestInvalidationLog();
        var plan = CreatePlan((_, _) =>
        {
            log.Append(new LiveTableDependency("sales", "orders"));
            return ValueTask.FromResult<IReadOnlyList<Row>>([new Row(1, "one")]);
        });
        await using var session = CreateSession(
            plan,
            log,
            new LiveQuerySessionOptions { MaximumInitialCatchUpPasses = 2 });

        await Assert.ThrowsAsync<LiveInitialCatchUpException>(async () =>
            await session.StartAsync(TestContext.Current.CancellationToken));
        Assert.False(session.Status.IsStarted);
        Assert.Equal(2, session.Status.AuthoritativeQueryCount);
    }

    private static LiveQuerySession<Row, int> CreateSession(
        LiveQueryPlan<Row, int> plan,
        ILiveInvalidationLog log,
        LiveQuerySessionOptions? options = null)
    {
        var arguments = plan.Bind(new Dictionary<string, object?> { ["tenant"] = "tenant-a" });
        return new LiveQuerySession<Row, int>(
            plan,
            arguments,
            new LiveSecurityScope("tenant:a", "policy:v1"),
            log,
            options: options);
    }

    private static LiveQueryPlan<Row, int> CreatePlan(
        Func<LiveQueryExecutionContext, CancellationToken, ValueTask<IReadOnlyList<Row>>> execute,
        int maximum = 10) =>
        new(
            "orders",
            "database",
            new string('a', 64),
            LiveQueryCapabilities.SingleTable |
                LiveQueryCapabilities.TenantFilter |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake,
            [new LiveTableDependency("sales", "orders")],
            [new LiveQueryParameter("tenant", typeof(string))],
            maximum,
            execute,
            static row => row.Id);

    private sealed record Row(int Id, string Value);

    private sealed class TestInvalidationLog : ILiveInvalidationLog
    {
        private readonly List<(long Cursor, LiveTableDependency Dependency)> _entries = [];
        private long _cursor;

        public ValueTask<LiveInvalidationCursor> GetCurrentCursorAsync(
            string databaseIdentity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new LiveInvalidationCursor(_cursor));
        }

        public ValueTask<bool> HasChangesAsync(
            string databaseIdentity,
            IReadOnlyCollection<LiveTableDependency> dependencies,
            LiveInvalidationCursor afterExclusive,
            LiveInvalidationCursor throughInclusive,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_entries.Any(entry =>
                entry.Cursor > afterExclusive.Value &&
                entry.Cursor <= throughInclusive.Value &&
                dependencies.Contains(entry.Dependency)));
        }

        public void Append(LiveTableDependency dependency)
        {
            _cursor++;
            _entries.Add((_cursor, dependency));
        }

        public void SetCursor(long cursor) => _cursor = cursor;
    }
}

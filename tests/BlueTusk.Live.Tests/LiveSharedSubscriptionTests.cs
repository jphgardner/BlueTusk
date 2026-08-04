namespace BlueTusk.Live.Tests;

public sealed class LiveSharedSubscriptionTests
{
    [Fact]
    public async Task Subscription_lifecycle_emits_metrics_and_balances_connected_clients()
    {
        var measurements =
            new System.Collections.Concurrent.ConcurrentQueue<(
                string Name,
                long Value,
                string? Outcome,
                string? Operation)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "BlueTusk.Live")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "bluetusk.live.connection.outcome")
                {
                    outcome = tag.Value?.ToString();
                }
            }

            string? operation = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "bluetusk.live.replay.operation")
                {
                    operation = tag.Value?.ToString();
                }
            }

            measurements.Enqueue((instrument.Name, value, outcome, operation));
        });
        listener.Start();

        await using var shared = Shared(
            new InvalidationLog(),
            new ReplayStore(),
            () => [new Row(1, "one")]);
        await shared.StartAsync(TestContext.Current.CancellationToken);
        var connected = await shared.ConnectAsync(
            0,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, shared.Status.ConnectedClients);

        await connected.Connection!.DisposeAsync();

        Assert.Equal(0, shared.Status.ConnectedClients);
        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.live.connections" &&
                item.Value == 1 &&
                item.Outcome == "connected");
        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.live.clients.active" &&
                item.Value == 1);
        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.live.clients.active" &&
                item.Value == -1);
        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.live.replay.bytes" &&
                item.Value > 0 &&
                item.Operation == "read");
    }

    [Fact]
    public async Task Matching_subscribers_share_one_query_and_resume_without_a_gap()
    {
        var invalidations = new InvalidationLog();
        var replay = new ReplayStore();
        IReadOnlyList<Row> rows = [new Row(1, "one")];
        var queryCount = 0;
        await using var shared = Shared(
            invalidations,
            replay,
            () =>
            {
                queryCount++;
                return rows;
            });
        await shared.StartAsync(TestContext.Current.CancellationToken);
        var first = await shared.ConnectAsync(0, TestContext.Current.CancellationToken);
        var second = await shared.ConnectAsync(0, TestContext.Current.CancellationToken);
        Assert.Equal(LiveSubscriptionConnectStatus.Connected, first.Status);
        Assert.Equal(LiveSubscriptionConnectStatus.Connected, second.Status);
        Assert.Single(first.Connection!.Replay);
        Assert.Single(second.Connection!.Replay);

        rows = [new Row(1, "ONE"), new Row(2, "two")];
        invalidations.Append();
        Assert.Equal(2, await shared.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, queryCount);
        Assert.Equal(4, shared.Status.FanOutDeliveries);
        Assert.Equal(2, shared.Status.ConnectedClients);
        Assert.Equal(2, shared.Status.ConnectionOpenAttempts);

        var resumed = await shared.ConnectAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal([2L, 3L], resumed.Connection!.Replay.Select(item => item.Sequence));
        await first.Connection.DisposeAsync();
        await second.Connection.DisposeAsync();
        await resumed.Connection.DisposeAsync();
        Assert.Equal(0, shared.Status.ConnectedClients);
    }

    [Fact]
    public async Task Slow_client_is_forced_to_reset_without_unbounded_buffering()
    {
        var invalidations = new InvalidationLog();
        var replay = new ReplayStore();
        IReadOnlyList<Row> rows = [new Row(1, "one")];
        await using var shared = Shared(
            invalidations,
            replay,
            () => rows,
            new LiveSharedSubscriptionOptions
            {
                SubscriberBufferCapacity = 1,
                SlowClientPolicy = LiveSlowClientPolicy.RequireReset,
            });
        await shared.StartAsync(TestContext.Current.CancellationToken);
        var connected = await shared.ConnectAsync(1, TestContext.Current.CancellationToken);

        rows = [new Row(1, "ONE"), new Row(2, "two")];
        invalidations.Append();
        _ = await shared.RefreshAsync(TestContext.Current.CancellationToken);

        var message = await ReadOneAsync(connected.Connection!, TestContext.Current.CancellationToken);
        Assert.Equal(LiveSubscriberMessageKind.ResetRequired, message.Kind);
        Assert.Equal(1, shared.Status.SlowClientDisconnects);
        Assert.Equal("slow-client-reset", shared.Status.LastDisconnectCode);
        Assert.Equal(0, shared.Status.SubscriberCount);
    }

    [Fact]
    public async Task Subscriber_and_replay_limits_fail_before_allocating_a_connection()
    {
        var invalidations = new InvalidationLog();
        var replay = new ReplayStore();
        IReadOnlyList<Row> rows = [new Row(1, "one")];
        await using var shared = Shared(
            invalidations,
            replay,
            () => rows,
            new LiveSharedSubscriptionOptions
            {
                MaximumSubscribers = 1,
                MaximumReplayEventsPerConnect = 1,
            });
        await shared.StartAsync(TestContext.Current.CancellationToken);
        var first = await shared.ConnectAsync(0, TestContext.Current.CancellationToken);
        Assert.Equal(LiveSubscriptionConnectStatus.Connected, first.Status);
        Assert.Equal(
            LiveSubscriptionConnectStatus.QuotaExceeded,
            (await shared.ConnectAsync(1, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, shared.Status.QuotaRejections);
        await first.Connection!.DisposeAsync();

        rows = [new Row(1, "ONE"), new Row(2, "two")];
        invalidations.Append();
        _ = await shared.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            LiveSubscriptionConnectStatus.ReplayLimitExceeded,
            (await shared.ConnectAsync(0, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Resume_tokens_and_registry_never_cross_security_scopes()
    {
        var replay = new ReplayStore();
        var invalidations = new InvalidationLog();
        await using var tenantA = Shared(invalidations, replay, () => [new Row(1, "one")], scope: "tenant:a");
        await using var tenantB = Shared(invalidations, replay, () => [new Row(1, "one")], scope: "tenant:b");
        await tenantA.StartAsync(TestContext.Current.CancellationToken);
        await tenantB.StartAsync(TestContext.Current.CancellationToken);
        var protector = new LiveResumeTokenProtector(
            [new LiveResumeTokenKey("primary", new byte[32], isPrimary: true)]);
        var token = protector.Protect(tenantA.Identity, 1, TimeSpan.FromMinutes(5));

        Assert.Equal(
            LiveSubscriptionConnectStatus.InvalidResumeToken,
            (await tenantB.ConnectWithTokenAsync(token, protector, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, tenantB.Status.ResumeAttempts);
        Assert.Equal(1, tenantB.Status.ResumeRejections);
        await using var registry = new LiveSharedSubscriptionRegistry();
        Assert.Same(tenantA, registry.GetOrAdd(tenantA));
        Assert.Same(tenantB, registry.GetOrAdd(tenantB));
        Assert.Equal(2, registry.Count);
        Assert.Equal(2, registry.GetStatuses().Count);
    }

    [Fact]
    public async Task Fresh_connection_gets_authoritative_reset_when_initial_replay_expired()
    {
        var replay = new ReplayStore();
        var invalidations = new InvalidationLog();
        IReadOnlyList<Row> rows = [new Row(1, "one")];
        await using var shared = Shared(invalidations, replay, () => rows);
        await shared.StartAsync(TestContext.Current.CancellationToken);
        replay.ExpireReads = true;
        rows = [new Row(2, "current")];

        var fresh = await shared.ConnectAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal(LiveSubscriptionConnectStatus.Connected, fresh.Status);
        var reset = Assert.Single(fresh.Connection!.Replay);
        Assert.Equal(LiveEventKind.ResultReset, reset.Kind);
        Assert.Equal(2, reset.Sequence);
        await fresh.Connection.DisposeAsync();
    }

    private static async ValueTask<LiveSubscriberMessage> ReadOneAsync(
        LiveSubscriptionConnection connection,
        CancellationToken cancellationToken)
    {
        await foreach (var message in connection.ReadAllAsync(cancellationToken))
        {
            return message;
        }

        throw new InvalidOperationException("The subscriber channel completed without a message.");
    }

    private static LiveSharedSubscription<Row, int> Shared(
        InvalidationLog invalidations,
        ILiveReplayStore replay,
        Func<IReadOnlyList<Row>> rows,
        LiveSharedSubscriptionOptions? options = null,
        string scope = "tenant:a")
    {
        var plan = new LiveQueryPlan<Row, int>(
            "orders",
            "database",
            new string('a', 64),
            LiveQueryCapabilities.SingleTable |
                LiveQueryCapabilities.TenantFilter |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake,
            [new LiveTableDependency("sales", "orders")],
            [],
            10,
            (_, _) => ValueTask.FromResult(rows()),
            static row => row.Id);
        var session = new LiveQuerySession<Row, int>(
            plan,
            LiveQueryArguments.Create([], new Dictionary<string, object?>()),
            new LiveSecurityScope(scope, "policy:v1"),
            invalidations);
        return new LiveSharedSubscription<Row, int>(session, replay, options);
    }

    private sealed record Row(int Id, string Value);

    private sealed class InvalidationLog : ILiveInvalidationLog
    {
        private long _cursor;

        public ValueTask<LiveInvalidationCursor> GetCurrentCursorAsync(
            string databaseIdentity,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveInvalidationCursor(_cursor));

        public ValueTask<bool> HasChangesAsync(
            string databaseIdentity,
            IReadOnlyCollection<LiveTableDependency> dependencies,
            LiveInvalidationCursor afterExclusive,
            LiveInvalidationCursor throughInclusive,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(throughInclusive > afterExclusive);

        public void Append() => _cursor++;
    }

    private sealed class ReplayStore : ILiveReplayStore
    {
        private readonly Dictionary<string, List<LiveReplayEvent>> _events = new(StringComparer.Ordinal);

        public bool ExpireReads { get; set; }

        public ValueTask<LiveReplayAppendResult> AppendAsync(
            LiveReplayAppendRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(request.Identity.Fingerprint, out var events))
            {
                events = [];
                _events.Add(request.Identity.Fingerprint, events);
            }

            if (events.Count != request.ExpectedLastSequence)
            {
                return ValueTask.FromResult(new LiveReplayAppendResult(
                    LiveReplayAppendStatus.SequenceConflict,
                    events.Count));
            }

            events.AddRange(request.Events);
            return ValueTask.FromResult(new LiveReplayAppendResult(
                LiveReplayAppendStatus.Stored,
                events.Count));
        }

        public ValueTask<LiveReplayReadResult> ReadAsync(
            LiveSubscriptionIdentity identity,
            long afterSequence,
            int maximumEvents,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ExpireReads)
            {
                ExpireReads = false;
                return ValueTask.FromResult(new LiveReplayReadResult(
                    LiveReplayReadStatus.Expired,
                    2,
                    _events.TryGetValue(identity.Fingerprint, out var expiredEvents) ? expiredEvents.Count : 0));
            }

            if (!_events.TryGetValue(identity.Fingerprint, out var events))
            {
                return ValueTask.FromResult(new LiveReplayReadResult(LiveReplayReadStatus.NotFound, 0, 0));
            }

            var available = events
                .Where(item => item.Sequence > afterSequence)
                .Take(maximumEvents)
                .ToArray();
            return ValueTask.FromResult(new LiveReplayReadResult(
                available.Length == 0 ? LiveReplayReadStatus.Current : LiveReplayReadStatus.Available,
                1,
                events.Count,
                available));
        }

        public ValueTask<int> PruneAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);
    }
}

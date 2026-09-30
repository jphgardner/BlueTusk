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
        var firstMessage = await ReadOneAsync(first.Connection!, TestContext.Current.CancellationToken);
        var secondMessage = await ReadOneAsync(second.Connection!, TestContext.Current.CancellationToken);
        Assert.Same(firstMessage, secondMessage);

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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Failed_initial_append_can_be_retried(bool stored, bool canceled)
    {
        var replay = new FailingReplayStore { FailNextAppend = true, StoreBeforeFailure = stored, CancelFailure = canceled };
        var invalidations = new InvalidationLog();
        await using var shared = Shared(invalidations, replay, () => [new Row(1, "initial")]);

        Assert.NotNull(await Record.ExceptionAsync(async () =>
            await shared.StartAsync(TestContext.Current.CancellationToken)));
        Assert.False(shared.Status.IsStarted);
        Assert.False(shared.Status.QuerySession.IsStarted);
        Assert.Equal(0, shared.Status.PersistedSequence);

        await shared.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(shared.Status.IsStarted);
        Assert.Equal(1, shared.Status.QuerySession.AuthoritativeQueryCount);
        Assert.Equal(1, shared.Status.QuerySession.LastSequence);
        Assert.Equal(1, shared.Status.PersistedSequence);
        Assert.Same(replay.Attempts[0].Events[0], replay.Attempts[1].Events[0]);
        var connection = await shared.ConnectAsync(0, TestContext.Current.CancellationToken);
        Assert.Single(connection.Connection!.Replay);
        await connection.Connection.DisposeAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Failed_refresh_append_retries_exact_payload_before_newer_changes(bool stored, bool canceled)
    {
        var replay = new FailingReplayStore { StoreBeforeFailure = stored, CancelFailure = canceled };
        var invalidations = new InvalidationLog();
        IReadOnlyList<Row> rows = [new Row(1, "before")];
        await using var shared = Shared(invalidations, replay, () => rows);
        await shared.StartAsync(TestContext.Current.CancellationToken);
        var connected = await shared.ConnectAsync(1, TestContext.Current.CancellationToken);
        await using var connection = connected.Connection!;
        rows = [new Row(1, "after")];
        invalidations.Append();
        replay.FailNextAppend = true;

        Assert.NotNull(await Record.ExceptionAsync(async () =>
            await shared.RefreshAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(0, shared.Status.QuerySession.Cursor.Value);
        Assert.Equal(1, shared.Status.QuerySession.LastSequence);
        Assert.Equal(1, shared.Status.PersistedSequence);
        Assert.Equal(0, shared.Status.FanOutDeliveries);

        rows = [new Row(1, "newer")];
        invalidations.Append();
        Assert.Equal(2, await shared.RefreshAsync(TestContext.Current.CancellationToken));
        var first = await ReadOneAsync(connection, TestContext.Current.CancellationToken);
        var second = await ReadOneAsync(connection, TestContext.Current.CancellationToken);
        Assert.Equal(2, first.Event!.Sequence);
        Assert.Equal(3, second.Event!.Sequence);
        Assert.Contains("after", System.Text.Encoding.UTF8.GetString(first.Event.Payload.Span), StringComparison.Ordinal);
        Assert.Contains("newer", System.Text.Encoding.UTF8.GetString(second.Event.Payload.Span), StringComparison.Ordinal);
        Assert.Same(replay.Attempts[1].Events[0], replay.Attempts[2].Events[0]);
        Assert.Equal(3, shared.Status.PersistedSequence);
        Assert.Empty((await replay.ReadAsync(shared.Identity, 3, 10)).Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_reset_append_is_recovered_before_connect(bool stored)
    {
        var replay = new FailingReplayStore { StoreBeforeFailure = stored };
        var invalidations = new InvalidationLog();
        IReadOnlyList<Row> rows = [new Row(1, "before")];
        await using var shared = Shared(invalidations, replay, () => rows);
        await shared.StartAsync(TestContext.Current.CancellationToken);
        rows = [new Row(2, "current")];
        replay.ExpireNextRead = true;
        replay.FailNextAppend = true;

        Assert.NotNull(await Record.ExceptionAsync(async () =>
            await shared.ConnectAsync(0, TestContext.Current.CancellationToken)));
        Assert.Equal(1, shared.Status.QuerySession.LastSequence);
        Assert.Equal(1, shared.Status.PersistedSequence);

        var connected = await shared.ConnectAsync(0, TestContext.Current.CancellationToken);
        await using var connection = connected.Connection!;
        Assert.Equal(LiveEventKind.ResultReset, connection.Replay[^1].Kind);
        Assert.Equal(2, connection.Replay[^1].Sequence);
        Assert.Equal(2, shared.Status.QuerySession.AuthoritativeQueryCount);
        Assert.Same(replay.Attempts[1].Events[0], replay.Attempts[2].Events[0]);
    }

    [Fact]
    public async Task Serialization_failure_retains_initial_proposal_without_advancing_state()
    {
        var row = new SerializationRow(1) { FailSerialization = true };
        var plan = new LiveQueryPlan<SerializationRow, int>(
            "orders", "database", new string('a', 64),
            LiveQueryCapabilities.SingleTable | LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake,
            [new LiveTableDependency("sales", "orders")], [], 10,
            (_, _) => ValueTask.FromResult<IReadOnlyList<SerializationRow>>([row]),
            static item => item.Id);
        var session = new LiveQuerySession<SerializationRow, int>(plan,
            LiveQueryArguments.Create([], new Dictionary<string, object?>()),
            new LiveSecurityScope("tenant:a", "policy:v1"), new InvalidationLog());
        var replay = new FailingReplayStore();
        await using var shared = new LiveSharedSubscription<SerializationRow, int>(session, replay);

        Assert.NotNull(await Record.ExceptionAsync(async () =>
            await shared.StartAsync(TestContext.Current.CancellationToken)));
        Assert.False(shared.Status.IsStarted);
        Assert.Equal(0, shared.Status.QuerySession.LastSequence);
        Assert.Equal(0, shared.Status.PersistedSequence);
        Assert.Empty(replay.Attempts);

        row.FailSerialization = false;
        await shared.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, shared.Status.QuerySession.AuthoritativeQueryCount);
        Assert.Equal(1, shared.Status.PersistedSequence);
        Assert.Single(replay.Attempts);
    }

    [Fact]
    public async Task Cancellation_after_successful_append_still_commits_and_publishes()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var replay = new FailingReplayStore();
        var invalidations = new InvalidationLog();
        IReadOnlyList<Row> rows = [new Row(1, "before")];
        await using var shared = Shared(invalidations, replay, () => rows);
        await shared.StartAsync(TestContext.Current.CancellationToken);
        var connected = await shared.ConnectAsync(1, TestContext.Current.CancellationToken);
        await using var connection = connected.Connection!;
        rows = [new Row(1, "after")];
        invalidations.Append();
        replay.AfterAppend = cancellation.Cancel;

        Assert.Equal(1, await shared.RefreshAsync(cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, shared.Status.QuerySession.Cursor.Value);
        Assert.Equal(2, shared.Status.QuerySession.LastSequence);
        Assert.Equal(2, shared.Status.PersistedSequence);
        Assert.Equal(2, (await ReadOneAsync(connection, TestContext.Current.CancellationToken)).Event!.Sequence);
        replay.AfterAppend = null;
        Assert.Equal(0, await shared.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, replay.Attempts.Count);
    }

    [Fact]
    public async Task Divergent_replay_fork_never_commits_pending_proposal()
    {
        var replay = new FailingReplayStore();
        var invalidations = new InvalidationLog();
        IReadOnlyList<Row> rows = [new Row(1, "before")];
        await using var shared = Shared(invalidations, replay, () => rows);
        await shared.StartAsync(TestContext.Current.CancellationToken);
        rows = [new Row(1, "after")];
        invalidations.Append();
        replay.FailNextAppend = true;
        await Assert.ThrowsAsync<IOException>(async () =>
            await shared.RefreshAsync(TestContext.Current.CancellationToken));
        var fork = LiveReplayJsonSerializer.Serialize(LiveResultDiffer.Initial<Row, int>(
            [new Row(1, "fork")], static row => row.Id, sequence: 2).Events[0]);
        _ = await replay.StoreAsync(new LiveReplayAppendRequest(shared.Identity, 1, [fork]));

        await Assert.ThrowsAsync<LiveReplaySequenceException>(async () =>
            await shared.RefreshAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<LiveReplaySequenceException>(async () =>
            await shared.ConnectAsync(0, TestContext.Current.CancellationToken));

        Assert.Equal(0, shared.Status.QuerySession.Cursor.Value);
        Assert.Equal(1, shared.Status.QuerySession.LastSequence);
        Assert.Equal(1, shared.Status.PersistedSequence);
        Assert.Equal(0, shared.Status.FanOutDeliveries);
        Assert.Same(replay.Attempts[1].Events[0], replay.Attempts[2].Events[0]);
    }

    [Fact]
    public async Task Metrics_observer_failures_do_not_interrupt_durability_or_fan_out()
    {
        var scope = new AsyncLocal<bool> { Value = true };
        var observations = 0;
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
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (scope.Value)
            {
                Interlocked.Increment(ref observations);
                throw new InvalidOperationException("Metrics exporter failed.");
            }
        });
        listener.SetMeasurementEventCallback<double>((_, _, _, _) =>
        {
            if (scope.Value)
            {
                Interlocked.Increment(ref observations);
                throw new InvalidOperationException("Metrics exporter failed.");
            }
        });
        listener.Start();
        var invalidations = new InvalidationLog();
        IReadOnlyList<Row> rows = [new Row(1, "before")];
        await using var shared = Shared(invalidations, new FailingReplayStore(), () => rows);
        await shared.StartAsync(TestContext.Current.CancellationToken);
        var connected = await shared.ConnectAsync(1, TestContext.Current.CancellationToken);
        await using var connection = connected.Connection!;
        rows = [new Row(1, "after")];
        invalidations.Append();

        Assert.Equal(1, await shared.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, (await ReadOneAsync(connection, TestContext.Current.CancellationToken)).Event!.Sequence);
        Assert.Equal(2, shared.Status.PersistedSequence);
        Assert.Equal(0, await shared.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.True(observations > 0);
    }

    private sealed record SerializationRow(int Id)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public bool FailSerialization { get; set; }
        public string Payload => FailSerialization ? throw new InvalidOperationException("Cannot serialize this row.") : "payload";
    }

    private sealed class FailingReplayStore : ILiveReplayStore
    {
        private readonly BlueTusk.Live.Testing.InMemoryLiveReplayStore _inner = new();

        public bool FailNextAppend { get; set; }
        public bool StoreBeforeFailure { get; init; }
        public bool CancelFailure { get; init; }
        public bool ExpireNextRead { get; set; }
        public Action? AfterAppend { get; set; }
        public List<LiveReplayAppendRequest> Attempts { get; } = [];

        public ValueTask<LiveReplayAppendResult> StoreAsync(LiveReplayAppendRequest request) =>
            _inner.AppendAsync(request);

        public async ValueTask<LiveReplayAppendResult> AppendAsync(
            LiveReplayAppendRequest request,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add(request);
            if (FailNextAppend)
            {
                FailNextAppend = false;
                if (StoreBeforeFailure)
                {
                    _ = await _inner.AppendAsync(request, cancellationToken);
                }

                if (CancelFailure)
                {
                    throw new OperationCanceledException("Replay outcome is uncertain.");
                }

                throw new IOException("Replay outcome is uncertain.");
            }

            var result = await _inner.AppendAsync(request, cancellationToken);
            AfterAppend?.Invoke();
            return result;
        }

        public ValueTask<LiveReplayReadResult> ReadAsync(
            LiveSubscriptionIdentity identity,
            long afterSequence,
            int maximumEvents,
            CancellationToken cancellationToken = default)
        {
            if (ExpireNextRead)
            {
                ExpireNextRead = false;
                return ValueTask.FromResult(new LiveReplayReadResult(LiveReplayReadStatus.Expired, 2, 1));
            }

            return _inner.ReadAsync(identity, afterSequence, maximumEvents, cancellationToken);
        }

        public ValueTask<int> PruneAsync(CancellationToken cancellationToken = default) =>
            _inner.PruneAsync(cancellationToken);
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

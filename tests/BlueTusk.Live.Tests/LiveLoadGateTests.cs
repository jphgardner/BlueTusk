using BlueTusk.Live.Testing;

namespace BlueTusk.Live.Tests;

public sealed class LiveLoadGateTests
{
    [Fact]
    public async Task One_hundred_invalidations_coalesce_and_fan_out_once_to_64_subscribers()
    {
        const int subscriberCount = 64;
        var dependency = new LiveTableDependency("sales", "orders");
        var invalidations = new InMemoryLiveInvalidationLog();
        var version = 0;
        var plan = new LiveQueryPlan<Row, int>(
            "load-gate",
            "database",
            new string('a', 64),
            LiveQueryCapabilities.SingleTable |
                LiveQueryCapabilities.TenantFilter |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake,
            [dependency],
            [],
            1,
            (_, _) => ValueTask.FromResult<IReadOnlyList<Row>>(
                [new Row(1, version == 0 ? "before" : "after")]),
            static row => row.Id);
        await using var session = new LiveQuerySession<Row, int>(
            plan,
            LiveQueryArguments.Create([], new Dictionary<string, object?>()),
            new LiveSecurityScope("tenant:load-gate", "policy:v1"),
            invalidations);
        await using var shared = new LiveSharedSubscription<Row, int>(
            session,
            new InMemoryLiveReplayStore(),
            new LiveSharedSubscriptionOptions
            {
                MaximumSubscribers = subscriberCount,
                SubscriberBufferCapacity = 1,
            });
        await shared.StartAsync(TestContext.Current.CancellationToken);
        var connections = new LiveSubscriptionConnection[subscriberCount];
        try
        {
            for (var index = 0; index < connections.Length; index++)
            {
                var result = await shared.ConnectAsync(
                    0,
                    TestContext.Current.CancellationToken);
                connections[index] = Assert.IsType<LiveSubscriptionConnection>(result.Connection);
            }

            version = 1;
            for (var index = 0; index < 100; index++)
            {
                _ = invalidations.Append("database", [dependency]);
            }

            Assert.Equal(1, await shared.RefreshAsync(TestContext.Current.CancellationToken));
            var status = shared.Status;

            Assert.Equal(subscriberCount, status.SubscriberCount);
            Assert.Equal(subscriberCount, status.FanOutDeliveries);
            Assert.Equal(2, status.QuerySession.AuthoritativeQueryCount);
            Assert.Equal(1, status.QuerySession.CoalescedInvalidationCount);
            Assert.Equal(2, status.PersistedSequence);
            Assert.Equal(2, status.PublishedEvents);
            Assert.Equal(100, status.QuerySession.Cursor.Value);
        }
        finally
        {
            foreach (var connection in connections)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
            }
        }
    }

    [Fact]
    public async Task Sixty_four_reconnect_publish_races_deliver_each_sequence_exactly_once()
    {
        var dependency = new LiveTableDependency("sales", "orders");
        var invalidations = new InMemoryLiveInvalidationLog();
        var version = 0;
        var plan = new LiveQueryPlan<Row, int>(
            "reconnect-race",
            "database",
            new string('a', 64),
            LiveQueryCapabilities.SingleTable |
                LiveQueryCapabilities.TenantFilter |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake,
            [dependency],
            [],
            1,
            (_, _) => ValueTask.FromResult<IReadOnlyList<Row>>([new Row(1, $"v{version}")]),
            static row => row.Id);
        await using var session = new LiveQuerySession<Row, int>(
            plan,
            LiveQueryArguments.Create([], new Dictionary<string, object?>()),
            new LiveSecurityScope("tenant:race", "policy:v1"),
            invalidations);
        await using var shared = new LiveSharedSubscription<Row, int>(
            session,
            new InMemoryLiveReplayStore(),
            new LiveSharedSubscriptionOptions { SubscriberBufferCapacity = 1 });
        await shared.StartAsync(TestContext.Current.CancellationToken);

        for (var iteration = 1; iteration <= 64; iteration++)
        {
            var afterSequence = shared.Status.PersistedSequence;
            version = iteration;
            _ = invalidations.Append("database", [dependency]);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connectTask = Task.Run(
                async () =>
                {
                    await start.Task;
                    return await shared.ConnectAsync(
                        afterSequence,
                        TestContext.Current.CancellationToken);
                },
                TestContext.Current.CancellationToken);
            var refreshTask = Task.Run(
                async () =>
                {
                    await start.Task;
                    return await shared.RefreshAsync(TestContext.Current.CancellationToken);
                },
                TestContext.Current.CancellationToken);
            start.SetResult();

            var connected = await connectTask;
            Assert.Equal(1, await refreshTask);
            await using var connection =
                Assert.IsType<LiveSubscriptionConnection>(connected.Connection);
            long observedSequence;
            if (connection.Replay.Count == 1)
            {
                observedSequence = connection.Replay[0].Sequence;
            }
            else
            {
                Assert.Empty(connection.Replay);
                observedSequence = (await ReadOneAsync(
                    connection,
                    TestContext.Current.CancellationToken)).Event!.Sequence;
            }

            Assert.Equal(afterSequence + 1, observedSequence);
        }

        Assert.Equal(65, shared.Status.PersistedSequence);
        Assert.Equal(65, shared.Status.QuerySession.AuthoritativeQueryCount);
        Assert.Equal(64, shared.Status.ConnectionOpenAttempts);
        Assert.Equal(0, shared.Status.SubscriberCount);
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

    private sealed record Row(int Id, string Value);
}

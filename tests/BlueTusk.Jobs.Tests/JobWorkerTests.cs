using System.Diagnostics;

namespace BlueTusk.Jobs.Tests;

public sealed class JobWorkerTests
{
    [Fact]
    public async Task TypedWorkerBoundsConcurrencyAndDrainsCooperativeHandlers()
    {
        await using var database = await JobDatabase.CreateAsync();
        var ids = new List<Guid>();
        for (int index = 0; index < 12; index++)
        {
            ids.Add(await database.Store.EnqueueAsync(database.Request()));
        }

        int active = 0;
        int peak = 0;
        int completed = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new JobHandlerRegistry().Register("test.v1", JobJsonContext.Default.TestJob, async (payload, context, cancellationToken) =>
        {
            Assert.Equal(42, payload.Value);
            Assert.Equal(database.Scope, context.Scope);
            int current = Interlocked.Increment(ref active);
            UpdateMaximum(ref peak, current);
            if (current == 3)
            {
                started.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref completed);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var worker = new JobWorker(database.Store, database.Scope, "typed", registry, Options(concurrency: 3));
        using var stop = new CancellationTokenSource();
        Task running = worker.RunAsync(stop.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(3, peak);
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync(stop.Token));
            release.TrySetResult();
            await WaitUntilAsync(async () =>
            {
                foreach (Guid id in ids)
                {
                    if ((await database.Store.ReadAsync(database.Scope, id))!.Status != JobStatus.Succeeded)
                    {
                        return false;
                    }
                }

                return true;
            });
            Assert.Equal(12, completed);
            Assert.Equal(3, peak);
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, active);
    }

    [Fact]
    public async Task HeartbeatsPreventReclaimAndCancellationRevokesCooperativeHandler()
    {
        await using var database = await JobDatabase.CreateAsync();
        Guid id = await database.Store.EnqueueAsync(database.Request());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new JobHandlerRegistry().Register("test.v1", JobJsonContext.Default.TestJob, async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                canceled.TrySetResult();
            }
        });
        var worker = new JobWorker(database.Store, database.Scope, "heartbeats", registry, Options(1) with
        {
            LeaseDuration = TimeSpan.FromMilliseconds(900),
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        });
        using var stop = new CancellationTokenSource();
        Task running = worker.RunAsync(stop.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromMilliseconds(1400));
            Assert.Empty(await database.Store.ClaimAsync(database.Scope, "other-worker", 1, TimeSpan.FromSeconds(30)));
            Assert.True(await database.Store.CancelAsync(database.Scope, id));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(JobStatus.Canceled, (await database.Store.ReadAsync(database.Scope, id))!.Status);
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task WorkerRetriesClassifiedAndUnexpectedFailuresWithoutPersistingExceptionText()
    {
        await using var database = await JobDatabase.CreateAsync();
        Guid id = await database.Store.EnqueueAsync(database.Request(attempts: 3));
        int calls = 0;
        var registry = new JobHandlerRegistry().Register("test.v1", JobJsonContext.Default.TestJob, (_, context, _) =>
        {
            Interlocked.Increment(ref calls);
            return context.Attempt switch
            {
                1 => ValueTask.FromException(new JobHandlerException("downstream_busy")),
                2 => ValueTask.FromException(new InvalidOperationException("password=secret raw exception")),
                _ => ValueTask.CompletedTask,
            };
        });
        var worker = new JobWorker(database.Store, database.Scope, "retry", registry, Options(1));
        using var stop = new CancellationTokenSource();
        Task running = worker.RunAsync(stop.Token);
        try
        {
            await WaitUntilAsync(async () => (await database.Store.ReadAsync(database.Scope, id))!.Status == JobStatus.Succeeded);
            Assert.Equal(3, calls);
            var history = await database.Store.ReadHistoryAsync(database.Scope, id);
            Assert.Equal("handler_failed", history[1].FailureCode);
            Assert.Equal("downstream_busy", history[2].FailureCode);
            Assert.DoesNotContain(history, attempt => attempt.FailureCode?.Contains("secret", StringComparison.Ordinal) == true);
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task InvalidTypedPayloadIsPermanentAndNeverInvokesHandler()
    {
        await using var database = await JobDatabase.CreateAsync();
        Guid id = await database.Store.EnqueueAsync(database.Request() with { Payload = "{invalid}"u8.ToArray() });
        int calls = 0;
        var registry = new JobHandlerRegistry().Register("test.v1", JobJsonContext.Default.TestJob, (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.CompletedTask;
        });
        var worker = new JobWorker(database.Store, database.Scope, "invalid", registry, Options(1));
        using var stop = new CancellationTokenSource();
        Task running = worker.RunAsync(stop.Token);
        try
        {
            await WaitUntilAsync(async () => (await database.Store.ReadAsync(database.Scope, id))!.Status == JobStatus.Failed);
            var snapshot = await database.Store.ReadAsync(database.Scope, id);
            Assert.Equal(1, snapshot!.Attempts);
            Assert.Equal("invalid_payload", snapshot.LastFailureCode);
            Assert.Equal(0, calls);
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static JobWorkerOptions Options(int concurrency) => new()
    {
        Concurrency = concurrency,
        PollInterval = TimeSpan.FromMilliseconds(10),
        DispatchRecurringSchedules = false,
        RetryPolicy = new JobRetryPolicy { InitialDelay = TimeSpan.FromMilliseconds(10), MaximumDelay = TimeSpan.FromMilliseconds(10), JitterFraction = 0 },
    };

    private static void UpdateMaximum(ref int location, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref location);
            if (current >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref location, value, current) != current);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), "The durable worker did not reach the expected state.");
            await Task.Delay(20);
        }
    }
}

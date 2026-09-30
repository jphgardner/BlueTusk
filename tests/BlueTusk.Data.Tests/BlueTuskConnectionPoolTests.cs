using System.Collections;
using System.Reflection;
using BlueTusk.Client;
using BlueTusk.Protocol;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskConnectionPoolTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Transaction_response_failure_retires_session_and_disposal_preserves_original_failure(
        bool asynchronous,
        bool commit)
    {
        var sessions = new List<FakePhysicalSession>();
        await using var pool = CreatePool(maximumSize: 1, factory: _ =>
        {
            var session = new FakePhysicalSession();
            sessions.Add(session);
            return ValueTask.FromResult<IBlueTuskPhysicalSession>(session);
        });
        await using var connection = new BlueTuskConnection("Host=localhost;Username=test;Database=test", pool);
        await connection.OpenAsync();
        var session = Assert.Single(sessions);
        var transaction = connection.BeginTransaction();
        Assert.True(transaction.TryStartServerTransaction());
        session.TransactionStatus = BlueTuskTransactionStatus.InTransaction;
        var original = new IOException("The transaction acknowledgement was lost.");
        session.TransactionFailure = original;

        var observed = asynchronous
            ? await Assert.ThrowsAsync<IOException>(() => commit ? transaction.CommitAsync() : transaction.RollbackAsync())
            : Assert.Throws<IOException>(() => { if (commit) { transaction.Commit(); } else { transaction.Rollback(); } });
        Assert.Same(original, observed);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.True(session.Disposed);
        Assert.True(transaction.IsCompleted);
        Assert.Null(connection.CurrentTransaction);
        await transaction.DisposeAsync();
        Assert.Equal([commit ? "COMMIT" : "ROLLBACK"], session.Commands);
        Assert.Equal(0, pool.Statistics.Busy);
        Assert.Equal(0, pool.Statistics.Idle);

        await connection.OpenAsync();
        Assert.Equal(2, sessions.Count);
        Assert.NotSame(session, sessions[1]);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task Transaction_pre_cancelled_completion_preserves_active_session_for_rollback()
    {
        var session = new FakePhysicalSession();
        await using var pool = CreatePool(factory: _ => ValueTask.FromResult<IBlueTuskPhysicalSession>(session));
        await using var connection = new BlueTuskConnection("Host=localhost;Username=test;Database=test", pool);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        Assert.True(transaction.TryStartServerTransaction());
        session.TransactionStatus = BlueTuskTransactionStatus.InTransaction;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.CommitAsync(cancellation.Token));
        Assert.Empty(session.Commands);
        Assert.False(transaction.IsCompleted);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        await transaction.RollbackAsync();
        Assert.Equal(["ROLLBACK"], session.Commands);
    }

    [Fact]
    public async Task Reuse_rolls_back_resets_and_reports_statistics()
    {
        var sessions = new List<FakePhysicalSession>();
        await using var pool = CreatePool(
            maximumSize: 2,
            factory: _ =>
            {
                var session = new FakePhysicalSession();
                sessions.Add(session);
                return ValueTask.FromResult<IBlueTuskPhysicalSession>(session);
            });

        var firstLease = await pool.RentAsync(CancellationToken.None);
        var firstSession = Assert.IsType<FakePhysicalSession>(firstLease.Session);
        firstSession.TransactionStatus = BlueTuskTransactionStatus.InTransaction;
        firstLease.MarkDirty();
        pool.Return(firstLease);

        var secondLease = await pool.RentAsync(CancellationToken.None);

        Assert.Same(firstSession, secondLease.Session);
        Assert.Equal(["ROLLBACK", "DISCARD ALL"], firstSession.Commands);
        Assert.Equal(
            new BlueTuskPoolStatistics(true, 0, 2, 1, 0, 1, 0, 1, 1, 0),
            pool.Statistics);
        pool.Return(secondLease);
        Assert.Equal(1, pool.Statistics.Idle);
        Assert.Equal(0, pool.Statistics.Busy);
    }

    [Fact]
    public async Task Maximum_size_bounds_checkouts_and_waiters_can_cancel()
    {
        await using var pool = CreatePool(maximumSize: 1);
        var lease = await pool.RentAsync(CancellationToken.None);
        using var cancellationSource = new CancellationTokenSource();
        var waiting = pool.RentAsync(cancellationSource.Token).AsTask();
        await WaitUntilAsync(() => pool.Statistics.Waiting == 1);

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, pool.Statistics.Total);
        Assert.Equal(1, pool.Statistics.Busy);
        pool.Return(lease);
        Assert.Equal(1, pool.Statistics.Idle);
    }

    [Fact]
    public async Task Returning_a_session_satisfies_an_existing_waiter_before_a_new_checkout()
    {
        await using var pool = CreatePool(maximumSize: 1);
        var firstLease = await pool.RentAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = pool.RentAsync(timeout.Token).AsTask();
        await WaitUntilAsync(() => pool.Statistics.Waiting == 1);
        await Task.Yield();

        pool.Return(firstLease);

        Assert.Null(pool.TryRent());
        var waiterLease = await waiting;
        Assert.Equal(0, pool.Statistics.Waiting);
        Assert.Equal(1, pool.Statistics.Busy);
        pool.Return(waiterLease);
    }

    [Fact]
    public async Task Warm_up_opens_the_configured_minimum()
    {
        await using var pool = CreatePool(minimumSize: 2, maximumSize: 3);

        await pool.WarmUpAsync(CancellationToken.None);

        Assert.Equal(2, pool.Statistics.Total);
        Assert.Equal(2, pool.Statistics.Idle);
        Assert.Equal(2, pool.Statistics.Opened);
    }

    [Fact]
    public async Task Idle_lifetime_discards_and_replaces_expired_sessions()
    {
        var timeProvider = new ManualTimeProvider();
        var sessions = new List<FakePhysicalSession>();
        await using var pool = CreatePool(
            maximumSize: 1,
            idleLifetime: TimeSpan.FromSeconds(1),
            connectionLifetime: TimeSpan.Zero,
            factory: _ =>
            {
                var session = new FakePhysicalSession();
                sessions.Add(session);
                return ValueTask.FromResult<IBlueTuskPhysicalSession>(session);
            },
            timeProvider: timeProvider);
        var firstLease = await pool.RentAsync(CancellationToken.None);
        var firstSession = Assert.IsType<FakePhysicalSession>(firstLease.Session);
        pool.Return(firstLease);
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        var secondLease = await pool.RentAsync(CancellationToken.None);

        Assert.NotSame(firstSession, secondLease.Session);
        Assert.True(firstSession.Disposed);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(2, pool.Statistics.Opened);
        Assert.Equal(1, pool.Statistics.Discarded);
        pool.Return(secondLease);
    }

    [Fact]
    public async Task Maximum_lifetime_discards_a_session_when_its_lease_returns()
    {
        var timeProvider = new ManualTimeProvider();
        await using var pool = CreatePool(
            maximumSize: 1,
            idleLifetime: TimeSpan.Zero,
            connectionLifetime: TimeSpan.FromSeconds(1),
            timeProvider: timeProvider);
        var lease = await pool.RentAsync(CancellationToken.None);
        var session = Assert.IsType<FakePhysicalSession>(lease.Session);
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        pool.Return(lease);

        Assert.True(session.Disposed);
        Assert.Equal(0, pool.Statistics.Total);
        Assert.Equal(1, pool.Statistics.Discarded);
    }

    [Fact]
    public async Task Failed_health_validation_is_replaced()
    {
        var sessions = new List<FakePhysicalSession>();
        await using var pool = CreatePool(
            maximumSize: 1,
            factory: _ =>
            {
                var session = new FakePhysicalSession();
                sessions.Add(session);
                return ValueTask.FromResult<IBlueTuskPhysicalSession>(session);
            });
        var firstLease = await pool.RentAsync(CancellationToken.None);
        var firstSession = Assert.IsType<FakePhysicalSession>(firstLease.Session);
        firstSession.FailReset = true;
        firstLease.MarkDirty();
        pool.Return(firstLease);

        var secondLease = await pool.RentAsync(CancellationToken.None);

        Assert.NotSame(firstSession, secondLease.Session);
        Assert.True(firstSession.Disposed);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(1, pool.Statistics.Discarded);
        pool.Return(secondLease);
    }

    [Fact]
    public async Task Untouched_lease_skips_server_reset_but_touched_lease_resets_before_reuse()
    {
        await using var pool = CreatePool(maximumSize: 1);
        var firstLease = await pool.RentAsync(CancellationToken.None);
        var session = Assert.IsType<FakePhysicalSession>(firstLease.Session);
        pool.Return(firstLease);

        var untouchedReuse = await pool.RentAsync(CancellationToken.None);
        Assert.Empty(session.Commands);
        untouchedReuse.MarkDirty();
        pool.Return(untouchedReuse);

        var touchedReuse = await pool.RentAsync(CancellationToken.None);
        Assert.Equal(["DISCARD ALL"], session.Commands);
        pool.Return(touchedReuse);
    }

    [Fact]
    public async Task Command_checkout_defers_reset_for_an_idle_dirty_session()
    {
        await using var pool = CreatePool(maximumSize: 1);
        var firstLease = await pool.RentAsync(CancellationToken.None);
        var session = Assert.IsType<FakePhysicalSession>(firstLease.Session);
        firstLease.MarkDirty();
        pool.Return(firstLease);

        var commandLease = await pool.RentForCommandAsync(CancellationToken.None);

        Assert.Same(firstLease, commandLease);
        Assert.True(commandLease.RequiresReset);
        Assert.Empty(session.Commands);
        Assert.Equal(1, pool.Statistics.Busy);
        Assert.Equal(0, pool.Statistics.Idle);
        pool.Return(commandLease);
    }

    [Fact]
    public async Task Command_checkout_still_resets_a_dirty_transaction()
    {
        await using var pool = CreatePool(maximumSize: 1);
        var firstLease = await pool.RentAsync(CancellationToken.None);
        var session = Assert.IsType<FakePhysicalSession>(firstLease.Session);
        session.TransactionStatus = BlueTuskTransactionStatus.InTransaction;
        firstLease.MarkDirty();
        pool.Return(firstLease);

        var commandLease = await pool.RentForCommandAsync(CancellationToken.None);

        Assert.False(commandLease.RequiresReset);
        Assert.Equal(["ROLLBACK", "DISCARD ALL"], session.Commands);
        pool.Return(commandLease);
    }

    [Fact]
    public async Task Failed_creation_releases_capacity_for_the_next_checkout()
    {
        var attempts = 0;
        await using var pool = CreatePool(
            maximumSize: 1,
            factory: _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new IOException("Simulated connection failure.");
                }

                return ValueTask.FromResult<IBlueTuskPhysicalSession>(new FakePhysicalSession());
            });

        await Assert.ThrowsAsync<IOException>(() => pool.RentAsync(CancellationToken.None).AsTask());
        var lease = await pool.RentAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(1, pool.Statistics.Total);
        pool.Return(lease);
    }

    [Fact]
    public async Task Clearing_marks_active_sessions_for_discard_on_return()
    {
        await using var pool = CreatePool(maximumSize: 1);
        var firstLease = await pool.RentAsync(CancellationToken.None);
        var firstSession = Assert.IsType<FakePhysicalSession>(firstLease.Session);

        await pool.ClearAsync();
        pool.Return(firstLease);
        var secondLease = await pool.RentAsync(CancellationToken.None);

        Assert.True(firstSession.Disposed);
        Assert.NotSame(firstSession, secondLease.Session);
        Assert.Equal(1, pool.Statistics.Discarded);
        pool.Return(secondLease);
    }

    [Fact]
    public async Task Disposing_the_pool_rejects_waiters_and_discards_returned_leases()
    {
        var pool = CreatePool(maximumSize: 1);
        var lease = await pool.RentAsync(CancellationToken.None);
        var session = Assert.IsType<FakePhysicalSession>(lease.Session);
        var waiting = pool.RentAsync(CancellationToken.None).AsTask();
        await WaitUntilAsync(() => pool.Statistics.Waiting == 1);

        await pool.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
        pool.Return(lease);
        Assert.True(session.Disposed);
        Assert.Equal(0, pool.Statistics.Total);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Disposal_completes_all_queued_checkouts_without_cancelling_their_callers(
        bool asynchronousDisposal, bool cancellableCallers)
    {
        await using var pool = CreatePool(maximumSize: 1);
        var lease = await pool.RentAsync(CancellationToken.None);
        var session = Assert.IsType<FakePhysicalSession>(lease.Session);
        using var cancellation = new CancellationTokenSource();
        var token = cancellableCallers ? cancellation.Token : CancellationToken.None;
        var waiters = Enumerable.Range(0, 64)
            .Select(_ => pool.RentAsync(token).AsTask()).ToArray();
        await WaitUntilAsync(() => pool.Statistics.Waiting == waiters.Length);

        if (asynchronousDisposal) { await pool.DisposeAsync(); }
        else { pool.Dispose(); }

        foreach (var waiter in waiters)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => waiter.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(0, pool.Statistics.Waiting);
        Assert.Equal(1, pool.Statistics.Busy);
        pool.Return(lease);
        Assert.True(session.Disposed);
        Assert.Equal(0, pool.Statistics.Total);
        Assert.Equal(0, pool.Statistics.Busy);
        Assert.Equal(0, pool.Statistics.Idle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_still_cancels_in_flight_session_creation_and_warmup(bool warmup)
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = CreatePool(
            minimumSize: warmup ? 1 : 0,
            maximumSize: 1,
            factory: async token =>
            {
                started.TrySetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new FakePhysicalSession();
            });
        using var caller = new CancellationTokenSource();
        Task pending = warmup
            ? pool.WarmUpAsync(caller.Token).AsTask()
            : pool.RentAsync(caller.Token).AsTask();
        var creationToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await pool.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(creationToken.IsCancellationRequested);
        Assert.False(caller.IsCancellationRequested);
        Assert.Equal(0, pool.Statistics.Total);
        Assert.Equal(0, pool.Statistics.Busy);
        Assert.Equal(0, pool.Statistics.Waiting);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 8)]
    [InlineData(true, 1)]
    [InlineData(true, 8)]
    public async Task Disposal_wakes_synchronous_and_asynchronous_waiters(
        bool asynchronousDisposal, int synchronousWaiterCount)
    {
        await using var pool = CreatePool(maximumSize: 1);
        var lease = await pool.RentAsync(CancellationToken.None);
        var session = Assert.IsType<FakePhysicalSession>(lease.Session);
        var completions = Enumerable.Range(0, synchronousWaiterCount)
            .Select(_ => new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var threads = completions.Select(completion => new Thread(() =>
        {
            completion.TrySetResult(Record.Exception(() =>
            {
                var unexpectedLease = pool.Rent();
                pool.Return(unexpectedLease);
            }));
        })
        { IsBackground = true }).ToArray();
        var asynchronousWaiters = Enumerable.Range(0, 8)
            .Select(_ => pool.RentAsync(CancellationToken.None).AsTask()).ToArray();
        try
        {
            foreach (var thread in threads) { thread.Start(); }
            // The counter increments before Monitor.Wait. Also require each
            // dedicated caller thread to have entered its blocking wait, so this
            // covers wakeup rather than only disposal before a checkout starts.
            await WaitUntilAsync(() =>
                pool.Statistics.Waiting == synchronousWaiterCount + asynchronousWaiters.Length &&
                threads.All(thread => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0));

            if (asynchronousDisposal) { await pool.DisposeAsync(); }
            else { pool.Dispose(); }

            var failures = await Task.WhenAll(completions.Select(completion => completion.Task))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(failures, failure => Assert.IsType<ObjectDisposedException>(failure));
            foreach (var waiter in asynchronousWaiters)
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(
                    () => waiter.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            Assert.Equal(0, pool.Statistics.Waiting);
            Assert.Equal(1, pool.Statistics.Busy);
        }
        finally
        {
            // A failing wakeup assertion must not strand test threads. Interrupt
            // only these dedicated test callers; normal completion never needs it.
            foreach (var thread in threads)
            {
                if (thread.IsAlive)
                {
                    try { thread.Interrupt(); }
                    catch (ThreadStateException) { }
                }
                if ((thread.ThreadState & ThreadState.Unstarted) == 0)
                {
                    Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
                }
            }
            pool.Return(lease);
        }
        Assert.True(session.Disposed);
        Assert.Equal(0, pool.Statistics.Total);
        Assert.Equal(0, pool.Statistics.Busy);
        Assert.Equal(0, pool.Statistics.Idle);
    }

    [Fact]
    public async Task Repeated_waiter_completion_cancellation_and_clear_preserve_lease_ownership()
    {
        await using var pool = CreatePool(maximumSize: 1);
        var lease = await pool.RentAsync(CancellationToken.None);
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var success = pool.RentAsync(CancellationToken.None).AsTask();
            using var cancellation = new CancellationTokenSource();
            var canceled = pool.RentAsync(cancellation.Token).AsTask();
            await WaitUntilAsync(() => pool.Statistics.Waiting == 2);

            // Exercise successful and exceptional async states repeatedly, including
            // a discarded generation. AsTask consumes each pooled ValueTask once;
            // the resulting Task must remain safe to await more than once.
            if (iteration % 4 == 0) { await pool.ClearAsync(); }
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            pool.Return(lease);
            lease = await success;
            Assert.Same(lease, await success);
            Assert.Equal(1, pool.Statistics.Total);
            Assert.Equal(1, pool.Statistics.Busy);
            Assert.Equal(0, pool.Statistics.Waiting);
        }
        pool.Return(lease);
        Assert.Equal(1, pool.Statistics.Idle);
        Assert.Equal(0, pool.Statistics.Busy);
    }

    [Theory]
    [InlineData("return")]
    [InlineData("cancel")]
    [InlineData("dispose")]
    [InlineData("replace")]
    public async Task Waiter_completion_runs_outside_the_pool_state_lock(string operation)
    {
        await using var pool = CreatePool(maximumSize: 1);
        var held = await pool.RentAsync(CancellationToken.None);
        var stateLock = GetPrivatePoolField(pool, "_stateSync");
        using var cancellation = new CancellationTokenSource();
        var completed = ObserveAsync();
        await WaitUntilAsync(() => pool.Statistics.Waiting == 1);
        var returned = false;
        try
        {
            switch (operation)
            {
                case "return":
                    returned = true;
                    pool.Return(held);
                    break;
                case "cancel":
                    cancellation.Cancel();
                    break;
                case "dispose":
                    await pool.DisposeAsync();
                    break;
                case "replace":
                    await pool.ClearAsync();
                    returned = true;
                    pool.Return(held);
                    break;
            }
            Assert.Equal(operation, await completed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, pool.Statistics.Waiting);
        }
        finally
        {
            if (!returned) { pool.Return(held); }
        }
        Assert.Equal(0, pool.Statistics.Busy);
        Assert.Equal(operation == "dispose" ? 0 : 1, pool.Statistics.Total);

        async Task<string> ObserveAsync()
        {
            try
            {
                var lease = await pool.RentAsync(cancellation.Token).ConfigureAwait(false);
                Assert.False(Monitor.IsEntered(stateLock));
                if (operation == "replace") { Assert.NotSame(held, lease); }
                pool.Return(lease);
                return operation == "replace" ? "replace" : "return";
            }
            catch (OperationCanceledException)
            {
                Assert.False(Monitor.IsEntered(stateLock));
                return "cancel";
            }
            catch (ObjectDisposedException)
            {
                Assert.False(Monitor.IsEntered(stateLock));
                return "dispose";
            }
        }
    }

    [Fact]
    public async Task Cancelling_a_middle_waiter_preserves_fifo_handoff_and_task_reuse()
    {
        await using var pool = CreatePool(maximumSize: 1);
        var held = await pool.RentAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var first = pool.RentAsync(CancellationToken.None).AsTask();
        var canceled = pool.RentAsync(cancellation.Token).AsTask();
        var last = pool.RentAsync(CancellationToken.None).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        pool.Return(held);
        var firstLease = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(firstLease, await first);
        Assert.False(last.IsCompleted);
        Assert.Null(pool.TryRent());
        pool.Return(firstLease);
        var lastLease = await last.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(lastLease, await last);
        pool.Return(lastLease);
        Assert.Equal(0, pool.Statistics.Waiting);
        Assert.Equal(0, pool.Statistics.Busy);
        Assert.Equal(1, pool.Statistics.Total);
    }

    [Fact]
    public async Task Cancellation_racing_handoff_cannot_complete_a_recycled_waiter()
    {
        await using var pool = CreatePool(maximumSize: 1);
        for (var iteration = 0; iteration < 256; iteration++)
        {
            var held = await pool.RentAsync(CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            var waiting = pool.RentAsync(cancellation.Token).AsTask();
            await Task.WhenAll(Task.Run(() => cancellation.Cancel()), Task.Run(() => pool.Return(held)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            try { pool.Return(await waiting.WaitAsync(TimeSpan.FromSeconds(5))); }
            catch (OperationCanceledException) { }

            var next = await pool.RentAsync(CancellationToken.None);
            var nextWaiting = pool.RentAsync(CancellationToken.None).AsTask();
            pool.Return(next);
            var nextLease = await nextWaiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(nextLease, await nextWaiting);
            pool.Return(nextLease);
            Assert.Equal(0, pool.Statistics.Waiting);
            Assert.Equal(0, pool.Statistics.Busy);
            Assert.Equal(1, pool.Statistics.Total);
        }
    }

    [Fact]
    public async Task Waiter_cache_is_bounded_after_a_large_checkout_burst()
    {
        await using var pool = CreatePool(maximumSize: 1);
        var held = await pool.RentAsync(CancellationToken.None);
        var waiting = Enumerable.Range(0, 512)
            .Select(_ => pool.RentAsync(CancellationToken.None).AsTask()).ToArray();
        foreach (var request in waiting)
        {
            pool.Return(held);
            held = await request.WaitAsync(TimeSpan.FromSeconds(5));
        }
        pool.Return(held);
        Assert.Equal(256, Assert.IsAssignableFrom<ICollection>(GetPrivatePoolField(pool, "_cachedWaiters")).Count);
        Assert.Equal(0, pool.Statistics.Waiting);
        Assert.Equal(0, pool.Statistics.Busy);
        Assert.Equal(1, pool.Statistics.Idle);
        await pool.DisposeAsync();
        Assert.Empty(Assert.IsAssignableFrom<ICollection>(GetPrivatePoolField(pool, "_cachedWaiters")));
    }

    private static object GetPrivatePoolField(BlueTuskConnectionPool pool, string name)
    {
        var field = typeof(BlueTuskConnectionPool).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(pool) ?? throw new InvalidOperationException("The pool field is null.");
    }

    private static BlueTuskConnectionPool CreatePool(
        int minimumSize = 0,
        int maximumSize = 10,
        TimeSpan? idleLifetime = null,
        TimeSpan? connectionLifetime = null,
        Func<CancellationToken, ValueTask<IBlueTuskPhysicalSession>>? factory = null,
        TimeProvider? timeProvider = null)
    {
        var settings = new BlueTuskConnectionStringBuilder
        {
            MinimumPoolSize = minimumSize,
            MaximumPoolSize = maximumSize,
            ConnectionIdleLifetime = idleLifetime ?? TimeSpan.FromMinutes(5),
            ConnectionLifetime = connectionLifetime ?? TimeSpan.FromHours(1),
        };
        return new BlueTuskConnectionPool(
            settings,
            factory ?? (_ => ValueTask.FromResult<IBlueTuskPhysicalSession>(new FakePhysicalSession())),
            timeProvider);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private sealed class FakePhysicalSession : IBlueTuskPhysicalSession
    {
        public bool IsOpen => !Disposed;

        public BlueTuskHostEndpoint Endpoint { get; } = new("localhost", 5432);

        public bool? IsPrimary => true;

        public bool? IsReadOnly => false;

        public IReadOnlyDictionary<string, string> Parameters { get; } =
            new Dictionary<string, string> { ["server_version"] = "test" };

        public BlueTuskTransactionStatus TransactionStatus { get; set; } = BlueTuskTransactionStatus.Idle;

        public List<string> Commands { get; } = [];

        public bool Disposed { get; private set; }

        public bool FailReset { get; set; }

        public Exception? TransactionFailure { get; set; }

        public BlueTuskQueryResult ExecuteSimpleQuery(string sql) =>
            ExecuteSimpleQueryAsync(sql).AsTask().GetAwaiter().GetResult();

        public ValueTask RefreshHostStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<BlueTuskQueryResult> ExecuteSimpleQueryAsync(
            string sql,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sql.StartsWith("SELECT t.oid::text", StringComparison.Ordinal))
            {
                static BlueTuskFieldDescription[] Fields(int count) => Enumerable.Range(0, count)
                    .Select(index => new BlueTuskFieldDescription($"field{index}", 0, 0, 25, -1, -1, 0)).ToArray();
                return ValueTask.FromResult(new BlueTuskQueryResult([
                    new BlueTuskResultSet(Fields(12), [], "SELECT 0"),
                    new BlueTuskResultSet(Fields(2), [], "SELECT 0"),
                    new BlueTuskResultSet(Fields(4), [], "SELECT 0"),
                    new BlueTuskResultSet(Fields(2), [new BlueTuskDataRow(["C"u8.ToArray(), "2"u8.ToArray()])], "SELECT 1"),
                ]));
            }
            if (FailReset && sql == "DISCARD ALL")
            {
                throw new IOException("Simulated health-validation failure.");
            }

            Commands.Add(sql);
            if (TransactionFailure is { } failure && sql is "COMMIT" or "ROLLBACK")
            {
                throw failure;
            }
            if (sql == "ROLLBACK")
            {
                TransactionStatus = BlueTuskTransactionStatus.Idle;
            }

            return ValueTask.FromResult(new BlueTuskQueryResult([]));
        }

        public ValueTask<BlueTuskQueryResult> ExecuteExtendedQueryAsync(
            string sql,
            IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
            bool useBinaryResults,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask PrepareStatementAsync(
            string statementName,
            string sql,
            IReadOnlyList<uint> parameterTypeOids,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BlueTuskQueryResult> ExecutePreparedStatementAsync(
            string statementName,
            IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
            bool useBinaryResults,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ClosePreparedStatementAsync(
            string statementName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BlueTuskQueryResult> ExecuteBatchAsync(
            IReadOnlyList<BlueTuskBatchQuery> queries,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BlueTuskQueryResult> ExecutePreparedBatchAsync(
            IReadOnlyList<BlueTuskPreparedBatchQuery> queries,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BlueTuskCopyResult> CopyInAsync(
            string sql,
            Stream source,
            Action<BlueTuskCopyResponse>? copyStarted,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BlueTuskCopyResult> CopyOutAsync(
            string sql,
            Stream destination,
            Action<BlueTuskCopyResponse>? copyStarted,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BlueTuskNotificationResponse> WaitForNotificationAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Cancel()
        {
        }

        public ValueTask CancelAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}

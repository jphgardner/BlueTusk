using BlueTusk.Client;
using BlueTusk.Protocol;
using BlueTusk.Security;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskMultiHostPoolTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unavailable_first_host_is_deferred_until_monotonic_recheck(bool asynchronous)
    {
        var fixture = new Fixture(); fixture.Failures[0] = new IOException("Unavailable");
        await using var pool = fixture.CreatePool();
        var lease = await Rent(pool, asynchronous); Assert.Equal("second", lease.Session.Endpoint.Host); pool.Return(lease);
        fixture.Failures[0] = null;
        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        lease = await Rent(pool, asynchronous); Assert.Equal("second", lease.Session.Endpoint.Host); pool.Return(lease);
        Assert.Equal(1, fixture.Opens[0]);
        fixture.Clock.MoveWallClock(TimeSpan.FromDays(400));
        lease = await Rent(pool, asynchronous); Assert.Equal("second", lease.Session.Endpoint.Host); pool.Return(lease);
        Assert.Equal(1, fixture.Opens[0]);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        lease = await Rent(pool, asynchronous); Assert.Equal("first", lease.Session.Endpoint.Host); pool.Return(lease);
        Assert.Equal(2, fixture.Opens[0]); Assert.Equal(0, pool.Statistics.Busy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unavailable_host_does_not_delay_waiting_for_healthy_saturated_pool(bool asynchronous)
    {
        var fixture = new Fixture(); fixture.Failures[0] = new IOException("Unavailable");
        await using var pool = fixture.CreatePool();
        var held = await Rent(pool, asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = Task.Run(async () => await Rent(pool, asynchronous, deadline.Token), deadline.Token);
        while (pool.Statistics.Waiting == 0) { await Task.Delay(1, deadline.Token); }
        Assert.Equal(1, fixture.Opens[0]);
        pool.Return(held);
        var recovered = await waiting.WaitAsync(deadline.Token);
        Assert.Same(held.Session, recovered.Session); Assert.Equal(1, fixture.Opens[0]); pool.Return(recovered);
        Assert.Equal(0, pool.Statistics.Busy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Wrapped_single_host_startup_failure_is_deferred(bool asynchronous)
    {
        var fixture = new Fixture();
        fixture.Failures[0] = new BlueTuskException("Startup failed", new AggregateException(new InvalidOperationException("Host failed", new IOException("Disconnected"))));
        await using var pool = fixture.CreatePool();
        var lease = await Rent(pool, asynchronous); pool.Return(lease); fixture.Failures[0] = null;
        lease = await Rent(pool, asynchronous); Assert.Equal("second", lease.Session.Endpoint.Host); pool.Return(lease);
        Assert.Equal(1, fixture.Opens[0]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Ambiguous_or_overdeep_failure_chain_is_not_cached(bool asynchronous, bool ambiguous)
    {
        var fixture = new Fixture();
        Exception failure = new IOException("Disconnected");
        if (ambiguous) { failure = new AggregateException(failure, new ArgumentException("Different cause")); }
        else { for (var depth = 0; depth < 20; depth++) { failure = new InvalidOperationException("Wrapper", failure); } }
        fixture.Failures[0] = failure;
        await using var pool = fixture.CreatePool();
        var lease = await Rent(pool, asynchronous); pool.Return(lease); fixture.Failures[0] = null;
        lease = await Rent(pool, asynchronous); Assert.Equal("first", lease.Session.Endpoint.Host); pool.Return(lease);
        Assert.Equal(2, fixture.Opens[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recent_failure_is_retried_if_other_host_also_fails(bool asynchronous)
    {
        var fixture = new Fixture(); fixture.Failures[0] = new IOException("Unavailable");
        await using var pool = fixture.CreatePool();
        var lease = await Rent(pool, asynchronous); pool.Return(lease);
        fixture.Failures[0] = null; fixture.Sessions[1].Single().RefreshFailure = new IOException("Second failed");
        lease = await Rent(pool, asynchronous);
        Assert.Equal("first", lease.Session.Endpoint.Host); Assert.Equal(2, fixture.Opens[0]); pool.Return(lease);
        Assert.True(fixture.Sessions[1].Single().Disposed); Assert.Equal(0, pool.Statistics.Busy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_clear_restores_configured_host_preference(bool asynchronous)
    {
        var fixture = new Fixture(); fixture.Failures[0] = new IOException("Unavailable");
        await using var pool = fixture.CreatePool();
        var lease = await Rent(pool, asynchronous); pool.Return(lease); fixture.Failures[0] = null;
        if (asynchronous) { await pool.ClearAsync(); } else { pool.Clear(); }
        lease = await Rent(pool, asynchronous); Assert.Equal("first", lease.Session.Endpoint.Host); pool.Return(lease);
        Assert.Equal(2, fixture.Opens[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Role_mismatch_is_rechecked_on_next_checkout(bool asynchronous)
    {
        var fixture = new Fixture(); fixture.ReadOnly[0] = true;
        await using var pool = fixture.CreatePool();
        var lease = await Rent(pool, asynchronous); Assert.Equal("second", lease.Session.Endpoint.Host); pool.Return(lease);
        fixture.Sessions[0].Single().ReadOnly = false;
        lease = await Rent(pool, asynchronous); Assert.Equal("first", lease.Session.Endpoint.Host); pool.Return(lease);
        Assert.Equal(1, fixture.Opens[0]); Assert.Equal(2, fixture.Sessions[0].Single().Refreshes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Authentication_rejection_stops_routing_and_is_not_cached_as_host_failure(bool asynchronous)
    {
        var fixture = new Fixture(); var rejection = new BlueTuskAuthenticationException("Rejected"); fixture.Failures[0] = rejection;
        await using var pool = fixture.CreatePool();
        Assert.Same(rejection, await Assert.ThrowsAsync<BlueTuskAuthenticationException>(async () => { _ = await Rent(pool, asynchronous); }));
        Assert.Equal(0, fixture.Opens[1]); fixture.Failures[0] = null;
        var lease = await Rent(pool, asynchronous); Assert.Equal("first", lease.Session.Endpoint.Host); pool.Return(lease);
    }

    [Fact]
    public async Task Concurrent_recheck_uses_one_probe_and_other_checkouts_can_use_healthy_host()
    {
        var fixture = new Fixture(); fixture.Failures[0] = new IOException("Unavailable");
        await using var pool = fixture.CreatePool();
        var lease = await pool.RentAsync(CancellationToken.None); pool.Return(lease);
        fixture.Failures[0] = null; fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.FirstGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = pool.RentAsync(CancellationToken.None).AsTask();
        await fixture.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lease = await pool.RentAsync(CancellationToken.None);
        Assert.Equal("second", lease.Session.Endpoint.Host); pool.Return(lease); Assert.Equal(2, fixture.Opens[0]);
        fixture.FirstGate.SetResult();
        lease = await probe.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("first", lease.Session.Endpoint.Host); pool.Return(lease);
        Assert.Equal(0, pool.Statistics.Busy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_host_recheck_waits_for_the_active_probe_instead_of_failing_empty(bool asynchronous)
    {
        var fixture = new Fixture { Host = "first" };
        fixture.Failures[0] = new IOException("Unavailable");
        await using var pool = fixture.CreatePool();
        await Assert.ThrowsAsync<BlueTuskException>(async () => { _ = await Rent(pool, asynchronous); });
        fixture.Failures[0] = null;
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.FirstGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var probe = Task.Run(async () => await Rent(pool, asynchronous, deadline.Token), deadline.Token);
        await fixture.FirstStarted.Task.WaitAsync(deadline.Token);
        var waiting = Task.Run(async () => await Rent(pool, asynchronous, deadline.Token), deadline.Token);
        await Task.Delay(50, deadline.Token);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(2, fixture.Opens[0]);

        fixture.FirstGate.SetResult();
        var ownerLease = await probe.WaitAsync(deadline.Token);
        pool.Return(ownerLease);
        var waitedLease = await waiting.WaitAsync(deadline.Token);
        pool.Return(waitedLease);
        Assert.Equal(2, fixture.Opens[0]);
        Assert.Equal(0, pool.Statistics.Busy);
    }

    [Fact]
    public async Task Cancelling_a_probe_waiter_does_not_cancel_the_owner()
    {
        var fixture = new Fixture { Host = "first" };
        fixture.Failures[0] = new IOException("Unavailable");
        await using var pool = fixture.CreatePool();
        await Assert.ThrowsAsync<BlueTuskException>(async () => { _ = await pool.RentAsync(CancellationToken.None); });
        fixture.Failures[0] = null;
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.FirstGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var probe = pool.RentAsync(deadline.Token).AsTask();
        await fixture.FirstStarted.Task.WaitAsync(deadline.Token);
        using var cancelled = new CancellationTokenSource();
        var waiting = pool.RentAsync(cancelled.Token).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        fixture.FirstGate.SetResult();
        var lease = await probe.WaitAsync(deadline.Token);
        pool.Return(lease);
        Assert.Equal(2, fixture.Opens[0]);
        Assert.Equal(0, pool.Statistics.Busy);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_defer_endpoint()
    {
        var fixture = new Fixture(); fixture.Failures[0] = new OperationCanceledException();
        await using var pool = fixture.CreatePool();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { _ = await pool.RentAsync(CancellationToken.None); });
        Assert.Equal(0, fixture.Opens[1]); fixture.Failures[0] = null;
        var lease = await pool.RentAsync(CancellationToken.None); Assert.Equal("first", lease.Session.Endpoint.Host); pool.Return(lease);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Healthy_preferred_target_fallback_avoids_failed_host_until_recheck(bool asynchronous)
    {
        var fixture = new Fixture { Target = BlueTuskTargetSessionAttributes.PreferPrimary };
        fixture.Primaries[1] = false; fixture.Failures[0] = new IOException("Unavailable");
        await using var pool = fixture.CreatePool();
        var lease = await Rent(pool, asynchronous); Assert.Equal("second", lease.Session.Endpoint.Host); pool.Return(lease);
        fixture.Failures[0] = null;
        lease = await Rent(pool, asynchronous); Assert.Equal("second", lease.Session.Endpoint.Host); pool.Return(lease);
        Assert.Equal(1, fixture.Opens[0]); fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        lease = await Rent(pool, asynchronous); Assert.Equal("first", lease.Session.Endpoint.Host); pool.Return(lease);
        Assert.Equal(2, fixture.Opens[0]); Assert.Equal(0, pool.Statistics.Busy);
    }

    private static ValueTask<BlueTuskPooledSession> Rent(BlueTuskMultiHostConnectionPool pool, bool asynchronous, CancellationToken token = default) =>
        asynchronous ? pool.RentAsync(token) : ValueTask.FromResult(pool.Rent());

    private sealed class Fixture
    {
        internal string Host { get; set; } = "first,second";
        internal TestClock Clock { get; } = new();
        internal Exception?[] Failures { get; } = new Exception?[2];
        internal int[] Opens { get; } = new int[2];
        internal bool[] ReadOnly { get; } = new bool[2];
        internal bool[] Primaries { get; } = [true, true];
        internal BlueTuskTargetSessionAttributes Target { get; set; } = BlueTuskTargetSessionAttributes.ReadWrite;
        internal List<FakePhysicalSession>[] Sessions { get; } = [[], []];
        internal TaskCompletionSource? FirstGate { get; set; }
        internal TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal BlueTuskMultiHostConnectionPool CreatePool() => new(new()
        { Host = Host, MaximumPoolSize = 1, TargetSessionAttributes = Target },
            timeProvider: Clock, poolFactory: settings =>
            {
                var index = settings.Host == "first" ? 0 : 1;
                return new(settings, token => Open(index, token), Clock,
                    synchronousSessionFactory: () => Open(index, CancellationToken.None).AsTask().GetAwaiter().GetResult());
            });
        private async ValueTask<IBlueTuskPhysicalSession> Open(int index, CancellationToken token)
        {
            Interlocked.Increment(ref Opens[index]);
            if (Failures[index] is { } exception) { throw exception; }
            if (index == 0 && FirstGate is { } gate) { FirstStarted.TrySetResult(); await gate.Task.WaitAsync(token); }
            var session = new FakePhysicalSession { Endpoint = new(index == 0 ? "first" : "second", 5432), ReadOnly = ReadOnly[index], Primary = Primaries[index] };
            Sessions[index].Add(session); return session;
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utc = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => _utc;
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
        internal void MoveWallClock(TimeSpan elapsed) => _utc += elapsed;
    }
    private sealed class FakePhysicalSession : IBlueTuskPhysicalSession
    {
        public bool IsOpen => !Disposed;

        public BlueTuskHostEndpoint Endpoint { get; init; } = new("localhost", 5432);

        public bool Primary { get; init; } = true;

        public bool? IsPrimary => Primary;

        public bool ReadOnly { get; set; }

        public bool? IsReadOnly => ReadOnly;

        public Exception? RefreshFailure { get; set; }

        public int Refreshes { get; private set; }

        public IReadOnlyDictionary<string, string> Parameters { get; } =
            new Dictionary<string, string> { ["server_version"] = "test" };

        public BlueTuskTransactionStatus TransactionStatus { get; set; } = BlueTuskTransactionStatus.Idle;

        public List<string> Commands { get; } = [];

        public bool Disposed { get; private set; }

        public bool FailReset { get; set; }

        public Exception? TransactionFailure { get; set; }

        public BlueTuskQueryResult ExecuteSimpleQuery(string sql) =>
            ExecuteSimpleQueryAsync(sql).AsTask().GetAwaiter().GetResult();

        public void RefreshHostState()
        {
            if (RefreshFailure is { } failure) { throw failure; }
            Refreshes++;
        }

        public ValueTask RefreshHostStateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); RefreshHostState(); return ValueTask.CompletedTask;
        }

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

}

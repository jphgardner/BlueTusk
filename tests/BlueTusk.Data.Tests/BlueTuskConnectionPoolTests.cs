using BlueTusk.Client;
using BlueTusk.Protocol;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskConnectionPoolTests
{
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
        pool.Return(firstLease);

        var secondLease = await pool.RentAsync(CancellationToken.None);

        Assert.NotSame(firstSession, secondLease.Session);
        Assert.True(firstSession.Disposed);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(1, pool.Statistics.Discarded);
        pool.Return(secondLease);
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

        public IReadOnlyDictionary<string, string> Parameters { get; } =
            new Dictionary<string, string> { ["server_version"] = "test" };

        public BlueTuskTransactionStatus TransactionStatus { get; set; } = BlueTuskTransactionStatus.Idle;

        public List<string> Commands { get; } = [];

        public bool Disposed { get; private set; }

        public bool FailReset { get; set; }

        public ValueTask<BlueTuskQueryResult> ExecuteSimpleQueryAsync(
            string sql,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailReset && sql == "DISCARD ALL")
            {
                throw new IOException("Simulated health-validation failure.");
            }

            Commands.Add(sql);
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

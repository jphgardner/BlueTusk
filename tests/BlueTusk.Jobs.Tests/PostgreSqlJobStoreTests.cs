namespace BlueTusk.Jobs.Tests;

public sealed class PostgreSqlJobStoreTests
{
    private static readonly int[] ExpectedHistoryAttempts = [3, 2];
    private static readonly string[] KnownJobTypes = ["test.v1"];

    [Fact]
    public async Task ClaimIsBoundedByAggregatePayloadBytesAsWellAsRows()
    {
        await using var database = await JobDatabase.CreateAsync(new JobStoreOptions { MaximumPayloadBytes = 4, MaximumClaimPayloadBytes = 8 });
        var request = new JobRequest { Scope = database.Scope, JobType = "bytes", Payload = new byte[4] };
        for (int index = 0; index < 3; index++)
        {
            _ = await database.Store.EnqueueAsync(request);
        }

        var first = await database.Store.ClaimAsync(database.Scope, "worker-a", 3, TimeSpan.FromMinutes(1));
        Assert.Equal(2, first.Count);
        Assert.Equal(8, first.Sum(lease => lease.Payload.Length));
        Assert.Single(await database.Store.ClaimAsync(database.Scope, "worker-b", 3, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task ConcurrentWorkersClaimDisjointBatches()
    {
        await using var database = await JobDatabase.CreateAsync();
        var ids = new HashSet<Guid>();
        for (int index = 0; index < 80; index++)
        {
            ids.Add(await database.Store.EnqueueAsync(database.Request()));
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            database.Store.ClaimAsync(database.Scope, "worker-" + index, 10, TimeSpan.FromSeconds(30)).AsTask()));
        var claimed = claims.SelectMany(batch => batch).ToArray();
        Assert.Equal(80, claimed.Length);
        Assert.Equal(80, claimed.Select(lease => lease.JobId).Distinct().Count());
        Assert.True(ids.SetEquals(claimed.Select(lease => lease.JobId)));
        Assert.All(claimed, lease => Assert.Equal(1, lease.Attempt));
        Assert.Empty(await database.Store.ClaimAsync(database.Scope, "ninth", 10, TimeSpan.FromSeconds(30)));
        foreach (var lease in claimed)
        {
            Assert.True(await database.Store.CompleteAsync(lease));
        }
    }

    [Fact]
    public async Task ExpiredLeaseCannotHeartbeatOrCompleteBeforeOrAfterReclaim()
    {
        await using var database = await JobDatabase.CreateAsync();
        Guid id = await database.Store.EnqueueAsync(database.Request());
        var first = Assert.Single(await database.Store.ClaimAsync(database.Scope, "old-worker", 1, TimeSpan.FromMinutes(1)));
        await database.ExecuteAsync("UPDATE {schema}.jobs SET lease_expires = clock_timestamp() - interval '1 second'");
        Assert.False(await database.Store.HeartbeatAsync(first, TimeSpan.FromMinutes(1)));
        Assert.False(await database.Store.CompleteAsync(first));
        Assert.False(await database.Store.FailAsync(first, "late_failure", TimeSpan.Zero));
        var second = Assert.Single(await database.Store.ClaimAsync(database.Scope, "new-worker", 1, TimeSpan.FromMinutes(1)));
        Assert.Equal(2, second.Attempt);
        Assert.True(second.FencingToken > first.FencingToken);
        Assert.False(await database.Store.CompleteAsync(first));
        Assert.True(await database.Store.CompleteAsync(second));
        Assert.Equal(JobStatus.Succeeded, (await database.Store.ReadAsync(database.Scope, id))!.Status);
        var history = await database.Store.ReadHistoryAsync(database.Scope, id);
        Assert.Equal("succeeded", history[0].Outcome);
        Assert.Equal("expired", history[1].Outcome);
    }

    [Fact]
    public async Task CrashedFinalAttemptBecomesFailedWithoutBeingClaimedAgain()
    {
        await using var database = await JobDatabase.CreateAsync();
        Guid id = await database.Store.EnqueueAsync(database.Request(attempts: 1));
        var lease = Assert.Single(await database.Store.ClaimAsync(database.Scope, "worker", 1, TimeSpan.FromMinutes(1)));
        await database.ExecuteAsync("UPDATE {schema}.jobs SET lease_expires = clock_timestamp() - interval '1 second'");
        Assert.Empty(await database.Store.ClaimAsync(database.Scope, "recovery", 1, TimeSpan.FromMinutes(1)));
        var snapshot = await database.Store.ReadAsync(database.Scope, id);
        Assert.Equal(JobStatus.Failed, snapshot!.Status);
        Assert.Equal("lease_expired", snapshot.LastFailureCode);
        Assert.Equal(1, snapshot.Attempts);
        Assert.False(await database.Store.CompleteAsync(lease));
    }

    [Fact]
    public async Task TransactionRollbackRemovesJobAndDeduplicationIdentity()
    {
        await using var database = await JobDatabase.CreateAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        Guid rolledBack;
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            rolledBack = await database.Store.EnqueueAsync(database.Request("order:42"), transaction, CancellationToken.None);
            Assert.Null(await database.Store.ReadAsync(database.Scope, rolledBack));
            await transaction.RollbackAsync();
        }

        Assert.Null(await database.Store.ReadAsync(database.Scope, rolledBack));
        Guid committed;
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            committed = await database.Store.EnqueueAsync(database.Request("order:42"), transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        Assert.NotEqual(rolledBack, committed);
        Assert.NotNull(await database.Store.ReadAsync(database.Scope, committed));
    }

    [Fact]
    public async Task ConcurrentDeduplicationReturnsOneIdentityAndRejectsDifferentPayload()
    {
        await using var database = await JobDatabase.CreateAsync();
        Guid[] ids = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => database.Store.EnqueueAsync(database.Request("same-key")).AsTask()));
        Assert.Single(ids.Distinct());
        var changed = database.Request("same-key") with { Payload = new byte[] { 9 } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.EnqueueAsync(changed).AsTask());
        Assert.Single(await database.Store.ClaimAsync(database.Scope, "worker", 128, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task TenantAndQueueBoundariesApplyToClaimReadCancelAndDeduplication()
    {
        await using var database = await JobDatabase.CreateAsync();
        var otherTenant = new JobScope("tenant-b", database.Scope.Queue);
        var otherQueue = new JobScope(database.Scope.Tenant, "other");
        Guid first = await database.Store.EnqueueAsync(database.Request("key"));
        Guid tenant = await database.Store.EnqueueAsync(database.Request("key", scope: otherTenant));
        Guid queue = await database.Store.EnqueueAsync(database.Request("key", scope: otherQueue));
        Assert.NotEqual(first, tenant);
        Assert.NotEqual(first, queue);
        Assert.Null(await database.Store.ReadAsync(otherTenant, first));
        Assert.False(await database.Store.CancelAsync(otherQueue, first));
        Assert.Equal(first, Assert.Single(await database.Store.ClaimAsync(database.Scope, "worker", 10, TimeSpan.FromMinutes(1))).JobId);
        Assert.Equal(tenant, Assert.Single(await database.Store.ClaimAsync(otherTenant, "worker", 10, TimeSpan.FromMinutes(1))).JobId);
        Assert.Equal(queue, Assert.Single(await database.Store.ClaimAsync(otherQueue, "worker", 10, TimeSpan.FromMinutes(1))).JobId);
    }

    [Fact]
    public async Task DelaysRetriesAndHistoryRemainBounded()
    {
        await using var database = await JobDatabase.CreateAsync(new JobStoreOptions { MaximumHistoryEntries = 2 });
        Guid id = await database.Store.EnqueueAsync(database.Request(attempts: 3) with { Delay = TimeSpan.FromHours(1) });
        Assert.Empty(await database.Store.ClaimAsync(database.Scope, "worker", 1, TimeSpan.FromMinutes(1)));
        await database.ExecuteAsync("UPDATE {schema}.jobs SET available_at = clock_timestamp() - interval '1 second'");
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var lease = Assert.Single(await database.Store.ClaimAsync(database.Scope, "worker", 1, TimeSpan.FromMinutes(1)));
            Assert.Equal(attempt, lease.Attempt);
            Assert.True(await database.Store.FailAsync(lease, "remote_busy", TimeSpan.FromHours(1)));
            Assert.Empty(await database.Store.ClaimAsync(database.Scope, "worker", 1, TimeSpan.FromMinutes(1)));
            await database.ExecuteAsync("UPDATE {schema}.jobs SET available_at = clock_timestamp() - interval '1 second'");
        }

        var snapshot = await database.Store.ReadAsync(database.Scope, id);
        Assert.Equal(JobStatus.Failed, snapshot!.Status);
        Assert.Equal("remote_busy", snapshot.LastFailureCode);
        var history = await database.Store.ReadHistoryAsync(database.Scope, id);
        Assert.Equal(2, history.Count);
        Assert.Equal(ExpectedHistoryAttempts, history.Select(attempt => attempt.Attempt));
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new BlueTusk.Data.BlueTuskCommand($"SELECT count(*) FROM \"{database.Options.Schema}\".job_attempts", connection);
        Assert.Equal(2L, await command.ExecuteScalarAsync<long>(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationFencesActiveLeaseAndRetentionReleasesDeduplication()
    {
        await using var database = await JobDatabase.CreateAsync();
        Guid id = await database.Store.EnqueueAsync(database.Request("retain-key"));
        var lease = Assert.Single(await database.Store.ClaimAsync(database.Scope, "worker", 1, TimeSpan.FromMinutes(1)));
        Assert.True(await database.Store.CancelAsync(database.Scope, id));
        Assert.False(await database.Store.CancelAsync(database.Scope, id));
        Assert.False(await database.Store.HeartbeatAsync(lease, TimeSpan.FromMinutes(1)));
        Assert.False(await database.Store.CompleteAsync(lease));
        Assert.Equal(JobStatus.Canceled, (await database.Store.ReadAsync(database.Scope, id))!.Status);
        Guid pending = await database.Store.EnqueueAsync(database.Request("pending"));
        Assert.Equal(1, await database.Store.PruneAsync(database.Scope, TimeSpan.Zero, 1));
        Assert.Null(await database.Store.ReadAsync(database.Scope, id));
        Assert.NotNull(await database.Store.ReadAsync(database.Scope, pending));
        Assert.Empty(await database.Store.ReadHistoryAsync(database.Scope, id));
        Assert.NotEqual(id, await database.Store.EnqueueAsync(database.Request("retain-key")));
    }

    [Fact]
    public async Task WorkerAllowlistDoesNotClaimUnknownJobTypes()
    {
        await using var database = await JobDatabase.CreateAsync();
        _ = await database.Store.EnqueueAsync(database.Request() with { JobType = "new.v2" });
        Guid known = await database.Store.EnqueueAsync(database.Request());
        var leases = await database.Store.ClaimAsync(database.Scope, "worker", 2, TimeSpan.FromMinutes(1), KnownJobTypes);
        Assert.Equal(known, Assert.Single(leases).JobId);
        Assert.Empty(await database.Store.ClaimAsync(database.Scope, "worker", 2, TimeSpan.FromMinutes(1), Array.Empty<string>()));
    }

    [Fact]
    public async Task SchemaInitializationRejectsIncompatiblePersistentLimits()
    {
        await using var database = await JobDatabase.CreateAsync();
        var incompatible = new PostgreSqlJobStore(database.DataSource, database.Options with { MaximumPayloadBytes = 1024 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => incompatible.InitializeAsync().AsTask());
        var same = new PostgreSqlJobStore(database.DataSource, database.Options);
        await same.InitializeAsync();
    }

    [Fact]
    public async Task DatabaseConstraintRejectsOversizeDirectWrite()
    {
        await using var database = await JobDatabase.CreateAsync(new JobStoreOptions { MaximumPayloadBytes = 64 });
        _ = await database.Store.EnqueueAsync(database.Request());
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => database.ExecuteAsync("UPDATE {schema}.jobs SET payload = decode(repeat('ff', 65), 'hex')"));
    }
}

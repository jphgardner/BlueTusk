using System.Collections.Concurrent;
using System.Diagnostics;

namespace BlueTusk.Jobs.Tests;

public sealed class JobBacklogTests
{
    [Fact]
    public async Task BoundedConcurrentClaimsDrainBacklogWithRetainedTerminalRows()
    {
        await using var database = await JobDatabase.CreateAsync();
        await database.ExecuteAsync("""
            INSERT INTO {schema}.jobs (tenant, queue, id, job_type, payload, available_at, maximum_attempts, status, completed_at)
            SELECT 'tenant-a', 'default', gen_random_uuid(), 'test.v1', ''::bytea, clock_timestamp(), 1, 2, clock_timestamp()
            FROM generate_series(1, 40000)
            """);
        await database.ExecuteAsync("""
            INSERT INTO {schema}.jobs (tenant, queue, id, job_type, payload, available_at, maximum_attempts)
            SELECT 'tenant-a', 'default', gen_random_uuid(), 'test.v1', ''::bytea, clock_timestamp(), 5
            FROM generate_series(1, 8000)
            """);
        await database.ExecuteAsync("ANALYZE {schema}.jobs");
        var identities = new ConcurrentDictionary<Guid, byte>();
        var elapsed = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async worker =>
        {
            while (true)
            {
                var claimed = await database.Store.ClaimAsync(database.Scope, "backlog-" + worker, 64, TimeSpan.FromMinutes(5));
                Assert.InRange(claimed.Count, 0, 64);
                if (claimed.Count == 0)
                {
                    return;
                }

                foreach (var lease in claimed)
                {
                    Assert.True(identities.TryAdd(lease.JobId, 0), "Two active workers claimed the same job.");
                }
            }
        }));
        Assert.Equal(8000, identities.Count);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(60), "Bounded backlog drain exceeded the regression timeout.");
    }
}

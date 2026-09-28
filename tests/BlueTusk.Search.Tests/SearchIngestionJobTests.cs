using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Jobs;
using BlueTusk.Search.Jobs;
using BlueTusk.Search.PgVector;

namespace BlueTusk.Search.Tests;

public sealed class SearchIngestionJobTests
{
    [Fact]
    public Task Durable_job_reopens_claims_embeds_and_preserves_metadata_and_ACL() => WithFixtureAsync(async fixture =>
    {
        using var metadata = JsonDocument.Parse("{\"source\":\"durable\"}");
        var document = new SearchDocument("tenant", "orders", "1", 1, "durable", "durable embedding recovery", principals: ["reader"], metadata: metadata.RootElement);
        var id = await fixture.Adapter.EnqueueUpsertAsync(document);
        Assert.Equal(id, await fixture.Adapter.EnqueueUpsertAsync(document));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Adapter.EnqueueUpsertAsync(new SearchDocument("tenant", "orders", "1", 1, "changed", "durable")));
        var reopenedJobs = new PostgreSqlJobStore(fixture.Source, fixture.JobOptions);
        var reopenedAdapter = new SearchIngestionJobs(reopenedJobs, fixture.Search, fixture.Adapter.Options);
        var lease = Assert.Single(await reopenedJobs.ClaimAsync(new JobScope("tenant", "search-ingestion"), "worker", 1, TimeSpan.FromMinutes(1)));
        await reopenedAdapter.ProcessLeaseAsync(lease);
        Assert.True(await reopenedJobs.CompleteAsync(lease));
        var page = await fixture.Search.SearchAsync(new SearchScope("tenant", "orders", ["reader"]), new SearchRequest { Mode = SearchMode.Hybrid, Text = "durable", Vector = new float[] { 1, 0, 0 } });
        Assert.Equal("durable", Assert.Single(page.Hits).Metadata.GetProperty("source").GetString());
        Assert.Empty((await fixture.Search.SearchAsync(new SearchScope("tenant", "orders", ["other"]), new SearchRequest { Text = "durable" })).Hits);
        Assert.Equal(1, fixture.Embeddings.Calls);
    });

    [Fact]
    public Task Search_commit_before_job_completion_recovers_with_no_second_embedding_and_stale_owner_is_fenced() => WithFixtureAsync(async fixture =>
    {
        var id = await fixture.Adapter.EnqueueUpsertAsync(new SearchDocument("tenant", "orders", "1", 1, "recover", "recover committed embedding", true));
        var scope = new JobScope("tenant", "search-ingestion");
        var old = Assert.Single(await fixture.Jobs.ClaimAsync(scope, "lost-owner", 1, TimeSpan.FromMinutes(1)));
        await fixture.Adapter.ProcessLeaseAsync(old);
        // Simulate loss after Search's commit and before Jobs completes the lease.
        await ExpireAsync(fixture, id);
        var recovered = Assert.Single(await new PostgreSqlJobStore(fixture.Source, fixture.JobOptions).ClaimAsync(scope, "new-owner", 1, TimeSpan.FromMinutes(1)));
        Assert.True(recovered.FencingToken > old.FencingToken);
        var rejected = await Assert.ThrowsAsync<JobHandlerException>(async () => await fixture.Adapter.ProcessLeaseAsync(old));
        Assert.Equal("search_lease_lost", rejected.FailureCode);
        await fixture.Adapter.ProcessLeaseAsync(recovered);
        Assert.True(await fixture.Jobs.CompleteAsync(recovered));
        Assert.False(await fixture.Jobs.CompleteAsync(old));
        Assert.Equal(1, fixture.Embeddings.Calls);
        Assert.Equal(JobStatus.Succeeded, (await fixture.Jobs.ReadAsync(scope, id))!.Status);
    });

    [Fact]
    public Task Transactional_admission_rolls_back_and_delayed_older_jobs_cannot_resurrect_deletes() => WithFixtureAsync(async fixture =>
    {
        Guid rolledBack;
        await using (var connection = await fixture.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            rolledBack = await fixture.Adapter.EnqueueUpsertInTransactionAsync(new SearchDocument("tenant", "orders", "rolled-back", 1, "none", "none"), transaction);
            await transaction.RollbackAsync();
        }

        Assert.Null(await fixture.Jobs.ReadAsync(new JobScope("tenant", "search-ingestion"), rolledBack));
        var deletion = await fixture.Adapter.EnqueueDeleteAsync("tenant", "orders", "1", 3);
        var older = await fixture.Adapter.EnqueueUpsertAsync(new SearchDocument("tenant", "orders", "1", 2, "stale", "stale resurrection", true));
        var leases = await fixture.Jobs.ClaimAsync(new JobScope("tenant", "search-ingestion"), "worker", 2, TimeSpan.FromMinutes(1));
        var deleteLease = Assert.Single(leases, item => item.JobId == deletion);
        var staleLease = Assert.Single(leases, item => item.JobId == older);
        await fixture.Adapter.ProcessLeaseAsync(deleteLease);
        await fixture.Adapter.ProcessLeaseAsync(staleLease);
        Assert.Equal(0, fixture.Embeddings.Calls);
        Assert.Empty((await fixture.Search.SearchAsync(new SearchScope("tenant", "orders"), new SearchRequest { Text = "stale" })).Hits);
        Assert.True(await fixture.Jobs.CompleteAsync(deleteLease));
        Assert.True(await fixture.Jobs.CompleteAsync(staleLease));
    });

    [Fact]
    public Task Contract_tenant_payload_and_lease_validation_precede_external_embeddings() => WithFixtureAsync(async fixture =>
    {
        var id = await fixture.Adapter.EnqueueUpsertAsync(new SearchDocument("tenant", "orders", "1", 1, "private", "private", principals: ["owner"]));
        var lease = Assert.Single(await fixture.Jobs.ClaimAsync(new JobScope("tenant", "search-ingestion"), "worker", 1, TimeSpan.FromMinutes(1)));
        var incompatible = new SearchIngestionJobs(fixture.Jobs, fixture.Search, fixture.Adapter.Options with { IndexContract = "different:model" });
        var contract = await Assert.ThrowsAsync<JobHandlerException>(async () => await incompatible.ProcessLeaseAsync(lease));
        Assert.Equal("search_contract_mismatch", contract.FailureCode);
        Assert.False(contract.Retryable);
        var wrongTenant = await Assert.ThrowsAsync<JobHandlerException>(async () => await fixture.Adapter.ProcessLeaseAsync(lease with { Scope = new JobScope("other", "search-ingestion") }));
        Assert.Equal("search_contract_mismatch", wrongTenant.FailureCode);
        var malformed = await Assert.ThrowsAsync<JobHandlerException>(async () => await fixture.Adapter.ProcessLeaseAsync(lease with { Payload = "[invalid"u8.ToArray() }));
        Assert.Equal("search_invalid_payload", malformed.FailureCode);
        await ExpireAsync(fixture, id);
        var expired = await Assert.ThrowsAsync<JobHandlerException>(async () => await fixture.Adapter.ProcessLeaseAsync(lease));
        Assert.Equal("search_lease_lost", expired.FailureCode);
        Assert.Equal(0, fixture.Embeddings.Calls);
    });

    [Fact]
    public Task Registered_worker_recovers_retryable_embedding_failure_and_completes_durable_work() => WithFixtureAsync(async fixture =>
    {
        fixture.Embeddings.FailNext = true;
        var id = await fixture.Adapter.EnqueueUpsertAsync(new SearchDocument("tenant", "orders", "1", 1, "retry", "retry embedding", true));
        var scope = new JobScope("tenant", "search-ingestion");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var worker = new JobWorker(fixture.Jobs, scope, "worker", fixture.Adapter.RegisterHandlers(new JobHandlerRegistry()), new JobWorkerOptions
        {
            Concurrency = 1, ClaimBatchSize = 1, PollInterval = TimeSpan.FromMilliseconds(10),
            RetryPolicy = new JobRetryPolicy { InitialDelay = TimeSpan.FromMilliseconds(10), MaximumDelay = TimeSpan.FromMilliseconds(20), JitterFraction = 0 },
            DispatchRecurringSchedules = false,
        });
        var run = worker.RunAsync(timeout.Token);
        try
        {
            while ((await fixture.Jobs.ReadAsync(scope, id, timeout.Token))!.Status is not JobStatus.Succeeded)
            {
                await Task.Delay(20, timeout.Token);
            }
        }
        finally
        {
            await timeout.CancelAsync();
            try { await run; } catch (OperationCanceledException) { }
        }

        Assert.Equal(2, fixture.Embeddings.Calls);
        Assert.Equal(2, (await fixture.Jobs.ReadAsync(scope, id))!.Attempts);
        Assert.Single((await fixture.Search.SearchAsync(new SearchScope("tenant", "orders"), new SearchRequest { Text = "retry" })).Hits);
    });

    [Fact]
    public async Task Serialized_admission_is_bounded_without_connecting()
    {
        await using var source = BlueTuskDataSource.Create("Host=127.0.0.1;Port=55418;Username=postgres;Password=postgres;Database=bluetusk_ecosystem;SSL Mode=Disable;Channel Binding=Disable");
        await using var search = new PostgreSqlSearchStore(source);
        var adapter = new SearchIngestionJobs(new PostgreSqlJobStore(source), search, new SearchIngestionJobOptions { IndexContract = "v1", MaxPayloadBytes = 256 });
        await Assert.ThrowsAsync<ArgumentException>(async () => await adapter.EnqueueUpsertAsync(new SearchDocument("tenant", "orders", "1", 1, "title", new string('x', 1024))));
        Assert.Throws<ArgumentException>(() => new SearchIngestionJobs(new PostgreSqlJobStore(source), search, adapter.Options with { Queue = "bad\0queue" }));
    }

    private static async Task ExpireAsync(Fixture fixture, Guid id)
    {
        await using var command = fixture.Source.CreateCommand($"UPDATE \"{fixture.JobOptions.Schema}\".jobs SET lease_expires = clock_timestamp() - interval '1 second' WHERE id = @id");
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task WithFixtureAsync(Func<Fixture, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure the disposable Search vector PostgreSQL fixture.");
        var jobSchema = "search_jobs_" + Guid.NewGuid().ToString("N");
        var searchSchema = "search_durable_" + Guid.NewGuid().ToString("N");
        await using var source = BlueTuskDataSource.Create(connectionString);
        var embeddings = new CountingEmbeddings();
        await using var search = new PostgreSqlSearchStore(source, new SearchStoreOptions { Schema = searchSchema }, new PgVectorSearchAdapter(3), embeddings);
        var options = new JobStoreOptions { Schema = jobSchema };
        var jobs = new PostgreSqlJobStore(source, options);
        try
        {
            await jobs.InitializeAsync();
            await search.InitializeAsync();
            var adapter = new SearchIngestionJobs(jobs, search, new SearchIngestionJobOptions { IndexContract = "test:model-v1:chunks-v1" });
            await test(new Fixture(source, jobs, options, search, adapter, embeddings));
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{jobSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{searchSchema}\" CASCADE");
            _ = await command.ExecuteNonQueryAsync();
        }
    }

    private sealed record Fixture(BlueTuskDataSource Source, PostgreSqlJobStore Jobs, JobStoreOptions JobOptions, PostgreSqlSearchStore Search, SearchIngestionJobs Adapter, CountingEmbeddings Embeddings);
    private sealed class CountingEmbeddings : ISearchEmbeddingProvider
    {
        public string ModelIdentity => "durable-test-v1";
        internal int Calls { get; private set; }
        internal bool FailNext { get; set; }
        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (FailNext)
            {
                FailNext = false;
                return ValueTask.FromException<IReadOnlyList<ReadOnlyMemory<float>>>(new IOException("embedding service transient failure"));
            }

            return ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(texts.Select(static _ => (ReadOnlyMemory<float>)new float[] { 1, 0, 0 }).ToArray());
        }
    }
}

using System.Data.Common;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Jobs;
using BlueTusk.Search.Jobs;
using BlueTusk.Search.OpenSearch;
using BlueTusk.Search.PgVector;

namespace BlueTusk.Search.Tests;

public sealed class EmbeddingCheckpointTests
{
    [Fact]
    public Task Completed_vectors_reopen_dedupe_text_and_partition_tenant_index_and_model() => WithFixtureAsync(async fixture =>
    {
        var embeddings = new TestEmbeddings();
        await using (var provider = fixture.Provider(embeddings))
        {
            await provider.InitializeAsync();
            var vectors = await provider.EmbedForScopeAsync(new SearchScope("tenant", "index"), ["same", "same", "other"]);
            Assert.Equal(3, vectors.Count);
            Assert.Equal(2, embeddings.Texts);
            Assert.Equal(vectors[0].ToArray(), vectors[1].ToArray());
        }
        await using var reopened = fixture.Provider(embeddings);
        await reopened.InitializeAsync();
        _ = await reopened.EmbedForScopeAsync(new SearchScope("tenant", "index", ["new-reader"]), ["same"]);
        Assert.Equal(1, embeddings.Calls);
        _ = await reopened.EmbedForScopeAsync(new SearchScope("other", "index"), ["same"]);
        _ = await reopened.EmbedForScopeAsync(new SearchScope("tenant", "other"), ["same"]);
        var different = new TestEmbeddings("checkpoint-model-v2");
        await using var differentModel = fixture.Provider(different);
        await differentModel.InitializeAsync();
        _ = await differentModel.EmbedForScopeAsync(new SearchScope("tenant", "index"), ["same"]);
        Assert.Equal(3, embeddings.Calls);
        Assert.Equal(1, different.Calls);
        Assert.Equal(5L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.checkpoints WHERE payload IS NOT NULL"));
        Assert.Equal(60L, await fixture.ScalarAsync($"SELECT reserved_bytes FROM {fixture.Schema}.metadata"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await reopened.EmbedAsync(["same"]));
    });

    [Fact]
    public Task Expired_owner_cannot_overwrite_replacement_checkpoint() => WithFixtureAsync(async fixture =>
    {
        var oldEmbeddings = new TestEmbeddings(block: true);
        var newEmbeddings = new TestEmbeddings(vector: [0, 1, 0]);
        await using var oldProvider = fixture.Provider(oldEmbeddings);
        await using var newProvider = fixture.Provider(newEmbeddings);
        await oldProvider.InitializeAsync(); await newProvider.InitializeAsync();
        var scope = new SearchScope("tenant", "index");
        var old = oldProvider.EmbedForScopeAsync(scope, ["same"]).AsTask();
        await oldEmbeddings.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<SearchBackpressureException>(async () => await newProvider.EmbedForScopeAsync(scope, ["same"]));
        await fixture.ExecuteAsync($"UPDATE {fixture.Schema}.checkpoints SET lease_expires=clock_timestamp()-interval '1 second'; UPDATE {fixture.Schema}.leases SET expires_at=clock_timestamp()-interval '1 second'");
        Assert.Equal(new float[] { 0, 1, 0 }, (await newProvider.EmbedForScopeAsync(scope, ["same"]))[0].ToArray());
        oldEmbeddings.Release.TrySetResult();
        await Assert.ThrowsAsync<SearchEmbeddingCheckpointOwnershipException>(() => old);
        Assert.Equal(new float[] { 0, 1, 0 }, (await newProvider.EmbedForScopeAsync(scope, ["same"]))[0].ToArray());
        Assert.Equal(1, newEmbeddings.Calls);
        Assert.Equal(0L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.leases"));
    });

    [Fact]
    public Task Database_admission_bounds_global_and_per_tenant_provider_batches() => WithFixtureAsync(async fixture =>
    {
        var options = fixture.Options with { MaxActiveProviderBatches = 2, MaxActiveProviderBatchesPerTenant = 1 };
        var firstEmbeddings = new TestEmbeddings(block: true);
        var secondEmbeddings = new TestEmbeddings(block: true);
        await using var first = fixture.Provider(firstEmbeddings, options);
        await using var second = fixture.Provider(secondEmbeddings, options);
        await using var third = fixture.Provider(new TestEmbeddings(), options);
        await first.InitializeAsync(); await second.InitializeAsync(); await third.InitializeAsync();
        var one = first.EmbedForScopeAsync(new SearchScope("one", "index"), ["first"]).AsTask();
        await firstEmbeddings.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<SearchBackpressureException>(async () => await third.EmbedForScopeAsync(new SearchScope("one", "other"), ["second"]));
        var two = second.EmbedForScopeAsync(new SearchScope("two", "index"), ["second"]).AsTask();
        await secondEmbeddings.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<SearchBackpressureException>(async () => await third.EmbedForScopeAsync(new SearchScope("three", "index"), ["third"]));
        Assert.Equal(2L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.leases"));
        firstEmbeddings.Release.TrySetResult(); secondEmbeddings.Release.TrySetResult();
        await Task.WhenAll(one, two);
        Assert.Equal(2L, await fixture.ScalarAsync($"SELECT records FROM {fixture.Schema}.metadata"));
        _ = await third.EmbedForScopeAsync(new SearchScope("one", "index"), ["first"]);
    });

    [Fact]
    public Task Input_vector_and_storage_bounds_and_expiration_preserve_exact_counters() => WithFixtureAsync(async fixture =>
    {
        var embeddings = new TestEmbeddings();
        var options = fixture.Options with { MaxRecords = 2, MaxReservedVectorBytes = 24, MaxTextBytes = 8, MaxBatchInputBytes = 16, MaxBatchTexts = 2 };
        await using var provider = fixture.Provider(embeddings, options);
        await provider.InitializeAsync();
        var scope = new SearchScope("tenant", "index");
        await Assert.ThrowsAsync<ArgumentException>(async () => await provider.EmbedForScopeAsync(scope, [new string('x', 9)]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await provider.EmbedForScopeAsync(scope, ["1", "2", "3"]));
        Assert.Equal(0, embeddings.Calls);
        _ = await provider.EmbedForScopeAsync(scope, ["first", "second"]);
        await Assert.ThrowsAsync<SearchBackpressureException>(async () => await provider.EmbedForScopeAsync(scope, ["third"]));
        Assert.Equal(24L, await fixture.ScalarAsync($"SELECT reserved_bytes FROM {fixture.Schema}.metadata"));
        await fixture.ExecuteAsync($"UPDATE {fixture.Schema}.checkpoints SET expires_at=clock_timestamp()-interval '1 second'");
        Assert.Equal(1, await provider.PruneExpiredAsync(1));
        Assert.Equal(12L, await fixture.ScalarAsync($"SELECT reserved_bytes FROM {fixture.Schema}.metadata"));
        Assert.Equal(1, await provider.PruneExpiredAsync(1));
        Assert.Equal(0L, await fixture.ScalarAsync($"SELECT records FROM {fixture.Schema}.metadata"));
        embeddings.Vector = [float.NaN, 0, 0];
        await Assert.ThrowsAsync<ArgumentException>(async () => await provider.EmbedForScopeAsync(scope, ["invalid"]));
        Assert.Equal(0L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.checkpoints WHERE payload IS NOT NULL"));
        Assert.Equal(0L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.leases"));
        Assert.Equal(1, await provider.PruneExpiredAsync());
    });

    [Fact]
    public Task Installed_schema_and_model_contracts_reject_drift_and_bound_model_registry() => WithFixtureAsync(async fixture =>
    {
        var options = fixture.Options with { MaxModels = 1 };
        await using var first = fixture.Provider(new TestEmbeddings(), options);
        await first.InitializeAsync();
        await using var admissionDrift = fixture.Provider(new TestEmbeddings(), options with { MaxRecords = options.MaxRecords + 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await admissionDrift.InitializeAsync());
        await using var dimensionDrift = fixture.Provider(new TestEmbeddings(), options with { Dimensions = 4 });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await dimensionDrift.InitializeAsync());
        await using var extraModel = fixture.Provider(new TestEmbeddings("another-model"), options);
        await Assert.ThrowsAsync<SearchBackpressureException>(async () => await extraModel.InitializeAsync());
    });

    [Fact]
    public Task Input_text_is_read_once_and_model_changes_during_external_work_cannot_poison_cache() => WithFixtureAsync(async fixture =>
    {
        var embeddings = new TestEmbeddings();
        await using var provider = fixture.Provider(embeddings);
        await provider.InitializeAsync();
        var scope = new SearchScope("tenant", "index");
        var texts = new ChangingInput();
        _ = await provider.EmbedForScopeAsync(scope, texts);
        Assert.Equal(1, texts.Reads);
        Assert.Equal(["original"], embeddings.LastTexts);
        _ = await provider.EmbedForScopeAsync(scope, ["original"]);
        Assert.Equal(1, embeddings.Calls);
        var changing = new TestEmbeddings(block: true);
        await using var changingProvider = fixture.Provider(changing);
        await changingProvider.InitializeAsync();
        var operation = changingProvider.EmbedForScopeAsync(scope, ["new-text"]).AsTask();
        await changing.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        changing.Identity = "changed-during-provider-call";
        changing.Release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Equal(1L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.checkpoints WHERE payload IS NOT NULL"));
        Assert.Equal(0L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.leases"));
        changing.Identity = embeddings.ModelIdentity;
        _ = await changingProvider.EmbedForScopeAsync(scope, ["new-text"]);
        Assert.Equal(2, changing.Calls);
    });

    [Fact]
    public Task Failed_checkpoint_batch_commit_rolls_back_every_vector_and_releases_owner() => WithFixtureAsync(async fixture =>
    {
        var embeddings = new TestEmbeddings();
        await using var provider = fixture.Provider(embeddings);
        await provider.InitializeAsync();
        await fixture.ExecuteAsync($"CREATE FUNCTION {fixture.Schema}.fail_complete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.payload IS NOT NULL THEN RAISE EXCEPTION 'injected checkpoint commit failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_complete BEFORE UPDATE ON {fixture.Schema}.checkpoints FOR EACH ROW EXECUTE FUNCTION {fixture.Schema}.fail_complete()");
        await Assert.ThrowsAnyAsync<DbException>(async () => await provider.EmbedForScopeAsync(new SearchScope("tenant", "index"), ["first", "second"]));
        Assert.Equal(0L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.checkpoints WHERE payload IS NOT NULL"));
        Assert.Equal(0L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.leases"));
        Assert.Equal(24L, await fixture.ScalarAsync($"SELECT reserved_bytes FROM {fixture.Schema}.metadata"));
        await fixture.ExecuteAsync($"DROP TRIGGER fail_complete ON {fixture.Schema}.checkpoints");
        Assert.Equal(2, (await provider.EmbedForScopeAsync(new SearchScope("tenant", "index"), ["first", "second"])).Count);
        Assert.Equal(2, embeddings.Calls);
        Assert.Equal(2L, await fixture.ScalarAsync($"SELECT records FROM {fixture.Schema}.metadata"));
    });

    [Fact]
    public Task Provider_timeout_releases_durable_admission_and_retry_requires_another_external_call() => WithFixtureAsync(async fixture =>
    {
        var embeddings = new TestEmbeddings(block: true);
        await using var provider = fixture.Provider(embeddings, fixture.Options with { ProviderTimeout = TimeSpan.FromMilliseconds(100), LeaseDuration = TimeSpan.FromSeconds(1) });
        await provider.InitializeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await provider.EmbedForScopeAsync(new SearchScope("tenant", "index"), ["timeout"]));
        Assert.Equal(0L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.leases"));
        Assert.Equal(0L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.checkpoints WHERE payload IS NOT NULL"));
        embeddings.Release.TrySetResult();
        _ = await provider.EmbedForScopeAsync(new SearchScope("tenant", "index"), ["timeout"]);
        Assert.Equal(2, embeddings.Calls);
    });

    [Fact]
    public Task One_replaced_batch_fence_rolls_back_all_completed_vectors() => WithFixtureAsync(async fixture =>
    {
        var embeddings = new TestEmbeddings(block: true);
        await using var provider = fixture.Provider(embeddings);
        await provider.InitializeAsync();
        var operation = provider.EmbedForScopeAsync(new SearchScope("tenant", "index"), ["first", "second"]).AsTask();
        await embeddings.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.ExecuteAsync($"UPDATE {fixture.Schema}.checkpoints SET fence=fence+1 WHERE text_hash=(SELECT text_hash FROM {fixture.Schema}.checkpoints ORDER BY text_hash LIMIT 1)");
        embeddings.Release.TrySetResult();
        await Assert.ThrowsAsync<SearchEmbeddingCheckpointOwnershipException>(() => operation);
        Assert.Equal(0L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.checkpoints WHERE payload IS NOT NULL"));
        Assert.Equal(0L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.leases"));
        Assert.Equal(24L, await fixture.ScalarAsync($"SELECT reserved_bytes FROM {fixture.Schema}.metadata"));
    });

    [Fact]
    public Task Bounded_eight_tenant_workload_checkpoints_1024_texts_in_batches_and_reopen_uses_only_cache() => WithFixtureAsync(async fixture =>
    {
        var embeddings = new TestEmbeddings();
        await using (var provider = fixture.Provider(embeddings))
        {
            await provider.InitializeAsync();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async tenant =>
            {
                var scope = new SearchScope("tenant-" + tenant, "index");
                for (var batch = 0; batch < 4; batch++)
                {
                    var texts = Enumerable.Range(batch * 32, 32).Select(i => i + ":" + new string('x', 1024)).ToArray();
                    Assert.Equal(32, (await provider.EmbedForScopeAsync(scope, texts)).Count);
                }
            }));
        }
        Assert.Equal(32, embeddings.Calls);
        Assert.Equal(1024, embeddings.Texts);
        Assert.Equal(1024L, await fixture.ScalarAsync($"SELECT records FROM {fixture.Schema}.metadata"));
        Assert.Equal(12_288L, await fixture.ScalarAsync($"SELECT reserved_bytes FROM {fixture.Schema}.metadata"));
        await using var reopened = fixture.Provider(embeddings);
        await reopened.InitializeAsync();
        for (var tenant = 0; tenant < 8; tenant++)
        {
            for (var batch = 0; batch < 4; batch++)
            {
                var texts = Enumerable.Range(batch * 32, 32).Select(i => i + ":" + new string('x', 1024)).ToArray();
                _ = await reopened.EmbedForScopeAsync(new SearchScope("tenant-" + tenant, "index"), texts);
            }
        }
        Assert.Equal(32, embeddings.Calls);
        Assert.Equal(0L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.leases"));
    });

    [Fact]
    public Task Jobs_recover_cache_commit_before_Search_commit_then_Search_commit_before_job_ack_across_reopen() => WithFixtureAsync(async fixture =>
    {
        var embeddings = new TestEmbeddings();
        var searchSchema = "checkpoint_search_" + Guid.NewGuid().ToString("N");
        var jobSchema = "checkpoint_jobs_" + Guid.NewGuid().ToString("N");
        var searchOptions = new SearchStoreOptions { Schema = searchSchema };
        var jobOptions = new JobStoreOptions { Schema = jobSchema };
        var adapterOptions = new SearchIngestionJobOptions { IndexContract = "checkpoint-model-v1:dimensions-3:chunks-v1" };
        var scope = new JobScope("tenant", "search-ingestion");
        try
        {
            var jobs = new PostgreSqlJobStore(fixture.Source, jobOptions);
            await jobs.InitializeAsync();
            Guid id; JobLease old;
            await using (var provider = fixture.Provider(embeddings))
            await using (var search = new PostgreSqlSearchStore(fixture.Source, searchOptions, new PgVectorSearchAdapter(3), provider))
            {
                await provider.InitializeAsync(); await search.InitializeAsync();
                using var metadata = JsonDocument.Parse("{\"recovery\":true}");
                var adapter = new SearchIngestionJobs(jobs, search, adapterOptions);
                id = await adapter.EnqueueUpsertAsync(new SearchDocument("tenant", "index", "document", 1, "recovery", "recover durable embeddings", principals: ["reader"], metadata: metadata.RootElement));
                old = Assert.Single(await jobs.ClaimAsync(scope, "lost-owner", 1, TimeSpan.FromMinutes(1)));
                await fixture.ExecuteAsync($"CREATE FUNCTION \"{searchSchema}\".fail_chunks() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected Search commit failure'; END $$; CREATE TRIGGER fail_chunks BEFORE INSERT ON \"{searchSchema}\".chunks FOR EACH ROW EXECUTE FUNCTION \"{searchSchema}\".fail_chunks()");
                await Assert.ThrowsAnyAsync<DbException>(async () => await adapter.ProcessLeaseAsync(old));
                Assert.Equal(1L, await fixture.ScalarAsync($"SELECT count(*) FROM {fixture.Schema}.checkpoints WHERE payload IS NOT NULL"));
                Assert.Equal(0L, await fixture.ScalarAsync($"SELECT count(*) FROM \"{searchSchema}\".documents"));
            }
            await fixture.ExecuteAsync($"DROP TRIGGER fail_chunks ON \"{searchSchema}\".chunks; UPDATE \"{jobSchema}\".jobs SET lease_expires=clock_timestamp()-interval '1 second'");
            JobLease recovered;
            await using (var provider = fixture.Provider(embeddings))
            await using (var search = new PostgreSqlSearchStore(fixture.Source, searchOptions, new PgVectorSearchAdapter(3), provider))
            {
                await provider.InitializeAsync(); await search.InitializeAsync();
                var reopenedJobs = new PostgreSqlJobStore(fixture.Source, jobOptions);
                recovered = Assert.Single(await reopenedJobs.ClaimAsync(scope, "second-owner", 1, TimeSpan.FromMinutes(1)));
                Assert.True(recovered.FencingToken > old.FencingToken);
                var adapter = new SearchIngestionJobs(reopenedJobs, search, adapterOptions);
                await adapter.ProcessLeaseAsync(recovered);
                Assert.Equal(1, embeddings.Calls);
                var page = await search.SearchAsync(new SearchScope("tenant", "index", ["reader"]), new SearchRequest { Text = "recovery" });
                Assert.True(Assert.Single(page.Hits).Metadata.GetProperty("recovery").GetBoolean());
                Assert.Single((await search.SearchAsync(new SearchScope("tenant", "index", ["reader"]), new SearchRequest { Mode = SearchMode.Vector, Text = "recover durable embeddings" })).Hits);
                Assert.Empty((await search.SearchAsync(new SearchScope("tenant", "index", ["other"]), new SearchRequest { Text = "recovery" })).Hits);
            }
            await fixture.ExecuteAsync($"UPDATE \"{jobSchema}\".jobs SET lease_expires=clock_timestamp()-interval '1 second'");
            await using (var provider = fixture.Provider(embeddings))
            await using (var search = new PostgreSqlSearchStore(fixture.Source, searchOptions, new PgVectorSearchAdapter(3), provider))
            {
                await provider.InitializeAsync(); await search.InitializeAsync();
                var reopenedJobs = new PostgreSqlJobStore(fixture.Source, jobOptions);
                var last = Assert.Single(await reopenedJobs.ClaimAsync(scope, "third-owner", 1, TimeSpan.FromMinutes(1)));
                Assert.True(last.FencingToken > recovered.FencingToken);
                await new SearchIngestionJobs(reopenedJobs, search, adapterOptions).ProcessLeaseAsync(last);
                Assert.True(await reopenedJobs.CompleteAsync(last));
                Assert.False(await reopenedJobs.CompleteAsync(old));
                Assert.False(await reopenedJobs.CompleteAsync(recovered));
                Assert.Equal(JobStatus.Succeeded, (await reopenedJobs.ReadAsync(scope, id))!.Status);
                Assert.Equal(1, embeddings.Calls);
            }
        }
        finally { await fixture.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{searchSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{jobSchema}\" CASCADE"); }
    }, vector: true);

    [Fact]
    public Task OpenSearch_ingestion_and_automatic_query_embeddings_use_scoped_checkpoints_across_reopen() => WithFixtureAsync(async fixture =>
    {
        var endpoint = Environment.GetEnvironmentVariable("BLUETUSK_SEARCH_OPENSEARCH_ENDPOINT") ?? throw new InvalidOperationException("Configure disposable OpenSearch.");
        var index = "checkpoint-opensearch-" + Guid.NewGuid().ToString("N");
        using var http = new HttpClient();
        var options = new OpenSearchStoreOptions { Endpoint = new Uri(endpoint), IndexName = index, Replicas = 0, VectorDimensions = 3 };
        var embeddings = new TestEmbeddings();
        try
        {
            await using (var provider = fixture.Provider(embeddings))
            await using (var search = new OpenSearchStore(http, options, provider))
            {
                await provider.InitializeAsync(); await search.InitializeAsync();
                _ = await search.UpsertAsync(new SearchDocument("tenant", "index", "first", 1, "cat", "cat cached", true));
                _ = await search.UpsertAsync(new SearchDocument("tenant", "index", "second", 1, "cat", "cat cached", true));
                _ = await search.UpsertAsync(new SearchDocument("other", "index", "third", 1, "cat", "cat cached", true));
                Assert.Equal(2, embeddings.Calls);
            }
            await using (var provider = fixture.Provider(embeddings))
            await using (var search = new OpenSearchStore(http, options, provider))
            {
                await provider.InitializeAsync(); await search.InitializeAsync();
                var scope = new SearchScope("tenant", "index");
                var ranked = await search.SearchAsync(scope, new SearchRequest { Mode = SearchMode.Vector, Text = "cat cached" });
                Assert.Equal(["first", "second"], (await search.ReadPageAsync(scope, ranked)).Hits.Select(static hit => hit.DocumentId).Order(StringComparer.Ordinal));
                Assert.Equal(2, embeddings.Calls);
                var otherScope = new SearchScope("tenant", "other");
                var empty = await search.SearchAsync(otherScope, new SearchRequest { Mode = SearchMode.Vector, Text = "cat cached" });
                Assert.Empty((await search.ReadPageAsync(otherScope, empty)).Hits);
                Assert.Equal(3, embeddings.Calls);
            }
        }
        finally
        {
            using var response = await http.DeleteAsync(new Uri(endpoint.TrimEnd('/') + "/" + index));
            response.EnsureSuccessStatusCode();
        }
    });

    private static async Task WithFixtureAsync(Func<Fixture, Task> test, bool vector = false)
    {
        var connectionString = Environment.GetEnvironmentVariable(vector ? "BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING" : "BLUETUSK_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure disposable Search PostgreSQL fixtures.");
        var schema = "embedding_checkpoint_" + Guid.NewGuid().ToString("N");
        await using var source = BlueTuskDataSource.Create(connectionString);
        var fixture = new Fixture(source, new SearchEmbeddingCheckpointOptions { Schema = schema });
        try { await test(fixture); }
        finally { await fixture.ExecuteAsync($"DROP SCHEMA IF EXISTS {fixture.Schema} CASCADE"); }
    }

    private sealed record Fixture(BlueTuskDataSource Source, SearchEmbeddingCheckpointOptions Options)
    {
        internal string Schema => '"' + Options.Schema + '"';
        internal PostgreSqlEmbeddingCheckpointProvider Provider(TestEmbeddings embeddings, SearchEmbeddingCheckpointOptions? options = null) => new(Source, embeddings, options ?? Options);
        internal async Task ExecuteAsync(string sql) { await using var command = Source.CreateCommand(sql); _ = await command.ExecuteNonQueryAsync(); }
        internal async Task<object?> ScalarAsync(string sql) { await using var command = Source.CreateCommand(sql); return await command.ExecuteScalarAsync(); }
    }
    private sealed class TestEmbeddings(string model = "checkpoint-model-v1", bool block = false, float[]? vector = null) : ISearchEmbeddingProvider
    {
        private int _calls;
        private int _texts;
        public string ModelIdentity => Identity;
        internal string Identity { get; set; } = model;
        internal int Calls => Volatile.Read(ref _calls);
        internal int Texts => Volatile.Read(ref _texts);
        internal float[] Vector { get; set; } = vector ?? [1, 0, 0];
        internal string[] LastTexts { get; private set; } = [];
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls); Interlocked.Add(ref _texts, texts.Count); Started.TrySetResult();
            LastTexts = texts.ToArray();
            if (block) { await Release.Task.WaitAsync(cancellationToken); }
            return texts.Select(_ => (ReadOnlyMemory<float>)Vector.ToArray()).ToArray();
        }
    }
    private sealed class ChangingInput : IReadOnlyList<string>
    {
        internal int Reads { get; private set; }
        public int Count => 1;
        public string this[int index] => index == 0 ? ++Reads == 1 ? "original" : "replacement" : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<string> GetEnumerator() => Enumerable.Repeat("replacement", 1).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

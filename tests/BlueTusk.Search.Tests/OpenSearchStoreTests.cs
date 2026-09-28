using System.Net;
using System.Text;
using System.Text.Json;
using BlueTusk.Search.OpenSearch;
using BlueTusk.Data;

namespace BlueTusk.Search.Tests;

public sealed class OpenSearchStoreTests
{
    [Fact]
    public Task Durable_rank_cursor_survives_both_store_reopens_and_rechecks_current_ACL_without_reranking() => WithFixtureAsync(fixture => WithCursorStoreAsync(async (source, cursors) =>
    {
        for (var i = 0; i < 4; i++)
        { _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "document-" + i, 1, "cat", "cat " + i, principals: ["reader"])); }
        var scope = new SearchScope("tenant", "knowledge", ["reader"]);
        var first = await fixture.Store.SearchDurableAsync(scope, new SearchRequest { Text = "cat", PageSize = 1 }, cursors);
        Assert.Equal("document-0", Assert.Single(first.Hits).DocumentId); Assert.NotNull(first.NextCursor);
        await using var restartedCursors = new PostgreSqlOpenSearchCursorStore(source, cursors.Options); await restartedCursors.InitializeAsync();
        await using var restartedSearch = new OpenSearchStore(fixture.Http, fixture.Store.Options, fixture.Embeddings); await restartedSearch.InitializeAsync();
        await Assert.ThrowsAsync<SearchCursorExpiredException>(async () => await restartedSearch.ContinueDurableSearchAsync(new SearchScope("other", "knowledge", ["reader"]), first.NextCursor!, restartedCursors));
        await Assert.ThrowsAsync<SearchCursorExpiredException>(async () => await restartedSearch.ContinueDurableSearchAsync(new SearchScope("tenant", "knowledge", ["owner"]), first.NextCursor!, restartedCursors));
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "document-1", 2, "cat", "cat revoked", principals: ["owner"]));
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "new-document", 1, "cat", "cat fresh", true));
        var continued = await restartedSearch.ContinueDurableSearchAsync(scope, first.NextCursor!, restartedCursors);
        Assert.Equal(["document-2", "document-3"], continued.Hits.Select(static hit => hit.DocumentId)); Assert.Null(continued.NextCursor);
        Assert.Equal(first.ExpiresAt, continued.ExpiresAt);
    }));

    [Fact]
    public Task Durable_cursor_capacity_serialized_bytes_DB_clock_expiry_and_bounded_cleanup_are_enforced() => WithFixtureAsync(fixture => WithCursorStoreAsync(async (source, cursors) =>
    {
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "a", 1, "cat", "cat", true));
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "b", 1, "cat", "cat", true));
        var scope = new SearchScope("tenant", "knowledge");
        var first = await fixture.Store.SearchDurableAsync(scope, new SearchRequest { Text = "cat", PageSize = 1 }, cursors);
        await Assert.ThrowsAsync<SearchBackpressureException>(async () => await fixture.Store.SearchDurableAsync(scope, new SearchRequest { Text = "cat" }, cursors));
        await using (var expire = source.CreateCommand($"UPDATE \"{cursors.Options.Schema}\".queries SET expires_at=clock_timestamp()-interval '1 second'"))
        { _ = await expire.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<SearchCursorExpiredException>(async () => await fixture.Store.ContinueDurableSearchAsync(scope, first.NextCursor!, cursors));
        Assert.Equal(1, await cursors.PruneExpiredAsync(1)); Assert.Equal(0, await cursors.PruneExpiredAsync(1));
        Assert.NotEmpty((await fixture.Store.SearchDurableAsync(scope, new SearchRequest { Text = "cat" }, cursors)).Hits);
    }, new OpenSearchCursorStoreOptions { MaxSnapshots = 1, MaxSnapshotsPerTenantIndex = 1, MaxSnapshotBytes = 1024, MaxRetainedBytes = 1024 }));

    [Fact]
    public Task Durable_contract_drift_and_index_recreation_invalidate_old_source_fences() => WithFixtureAsync(fixture => WithCursorStoreAsync(async (source, cursors) =>
    {
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "a", 1, "cat", "cat", true));
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "b", 1, "cat", "cat", true));
        var scope = new SearchScope("tenant", "knowledge");
        await using var tiny = new PostgreSqlOpenSearchCursorStore(source, cursors.Options with { MaxCandidates = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tiny.InitializeAsync());
        var first = await fixture.Store.SearchDurableAsync(scope, new SearchRequest { Text = "cat", PageSize = 1 }, cursors);
        using (var deleted = await fixture.Http.DeleteAsync(new Uri(fixture.Store.Options.Endpoint.ToString().TrimEnd('/') + "/" + fixture.Store.Options.IndexName)))
        { deleted.EnsureSuccessStatusCode(); }
        await fixture.Store.InitializeAsync();
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "a", 1, "cat changed", "cat changed", true));
        await Assert.ThrowsAsync<SearchCursorExpiredException>(async () => await fixture.Store.ContinueDurableSearchAsync(scope, first.NextCursor!, cursors));
    }));

    [Fact]
    public Task Serialized_rank_payload_budget_rejects_before_persisting_a_cursor() => WithFixtureAsync(fixture => WithCursorStoreAsync(async (source, cursors) =>
    {
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "a", 1, "cat", "cat " + new string('x', 80), true));
        await Assert.ThrowsAsync<SearchBackpressureException>(async () => await fixture.Store.SearchDurableAsync(new SearchScope("tenant", "knowledge"), new SearchRequest { Text = "cat" }, cursors));
        await using var count = source.CreateCommand($"SELECT retained_count FROM \"{cursors.Options.Schema}\".metadata WHERE singleton");
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }, new OpenSearchCursorStoreOptions { MaxSnapshotBytes = 128 }));

    [Fact]
    public Task Cursor_insert_fault_rolls_back_retention_counters_and_does_not_consume_capacity() => WithFixtureAsync(fixture => WithCursorStoreAsync(async (source, cursors) =>
    {
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "a", 1, "cat", "cat", true));
        await using (var inject = source.CreateCommand($"""
            CREATE FUNCTION "{cursors.Options.Schema}".fail_cursor() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected cursor fault'; END $$;
            CREATE TRIGGER fail_cursor BEFORE INSERT ON "{cursors.Options.Schema}".queries FOR EACH ROW EXECUTE FUNCTION "{cursors.Options.Schema}".fail_cursor()
            """)) { _ = await inject.ExecuteNonQueryAsync(); }
        var scope = new SearchScope("tenant", "knowledge");
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(async () => await fixture.Store.SearchDurableAsync(scope, new SearchRequest { Text = "cat" }, cursors));
        await using (var count = source.CreateCommand($"SELECT retained_count FROM \"{cursors.Options.Schema}\".metadata WHERE singleton"))
        { Assert.Equal(0L, await count.ExecuteScalarAsync()); }
        await using (var remove = source.CreateCommand($"DROP TRIGGER fail_cursor ON \"{cursors.Options.Schema}\".queries")) { _ = await remove.ExecuteNonQueryAsync(); }
        Assert.Single((await fixture.Store.SearchDurableAsync(scope, new SearchRequest { Text = "cat" }, cursors)).Hits);
    }, new OpenSearchCursorStoreOptions { MaxSnapshots = 1 }));

    [Fact]
    public Task Approximate_nested_Lucene_HNSW_filters_tenant_index_and_ACL_before_bounded_candidates() => WithFixtureAsync(async fixture =>
    {
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "visible-cat", 1, "cat", "cat", principals: ["reader"]));
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "visible-dog", 1, "dog", "dog", true));
        for (var i = 0; i < 12; i++)
        {
            _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "private-" + i, 1, "cat", "cat", principals: ["owner"]));
            _ = await fixture.Store.UpsertAsync(new SearchDocument("other", "knowledge", "other-" + i, 1, "cat", "cat", true));
        }
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "other", "wrong-index", 1, "cat", "cat", true));
        var scope = new SearchScope("tenant", "knowledge", ["reader"]);
        var approximate = await fixture.Store.SearchAsync(scope, new SearchRequest { Mode = SearchMode.Vector, Vector = new float[] { 1, 0, 0 }, ApproximateVectorSearch = true, CandidateLimit = 2 });
        Assert.Equal(["visible-cat", "visible-dog"], (await fixture.Store.ReadPageAsync(scope, approximate)).Hits.Select(static hit => hit.DocumentId));
        var hybrid = await fixture.Store.SearchAsync(scope, new SearchRequest { Mode = SearchMode.Hybrid, Text = "cat", Vector = new float[] { 1, 0, 0 }, ApproximateVectorSearch = true, CandidateLimit = 2 });
        Assert.Equal("visible-cat", (await fixture.Store.ReadPageAsync(scope, hybrid)).Hits[0].DocumentId);
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "visible-cat", 2, "cat", "cat", principals: ["owner"]));
        Assert.Equal("visible-dog", Assert.Single((await fixture.Store.ReadPageAsync(scope, approximate)).Hits).DocumentId);
    });

    [Fact]
    public Task Real_OpenSearch_nested_fulltext_vector_and_hybrid_apply_tenant_index_and_ACL_filters() => WithFixtureAsync(async fixture =>
    {
        using var metadata = JsonDocument.Parse("{\"origin\":\"integration\"}");
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "cat", 1, "cat notes", "cat habits and feline food", principals: ["reader"], metadata: metadata.RootElement));
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "dog", 1, "dog notes", "dog habits and food", true));
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "private", 1, "cat private", "cat private details", principals: ["owner"]));
        _ = await fixture.Store.UpsertAsync(new SearchDocument("other", "knowledge", "cat", 1, "cat other", "cat", true));
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "other", "cat", 1, "cat other index", "cat", true));
        var scope = new SearchScope("tenant", "knowledge", ["reader"]);
        var text = await fixture.Store.SearchAsync(scope, new SearchRequest { Text = "cat" });
        Assert.Equal("cat", Assert.Single((await fixture.Store.ReadPageAsync(scope, text)).Hits).DocumentId);
        var vector = await fixture.Store.SearchAsync(scope, new SearchRequest { Mode = SearchMode.Vector, Vector = new float[] { 1, 0, 0 } });
        var vectorPage = await fixture.Store.ReadPageAsync(scope, vector);
        Assert.Equal("cat", vectorPage.Hits[0].DocumentId);
        Assert.Equal(["cat", "dog"], vectorPage.Hits.Select(static hit => hit.DocumentId).Order(StringComparer.Ordinal));
        var hybrid = await fixture.Store.SearchAsync(scope, new SearchRequest { Mode = SearchMode.Hybrid, Text = "cat", Vector = new float[] { 1, 0, 0 } });
        Assert.Equal("integration", (await fixture.Store.ReadPageAsync(scope, hybrid)).Hits[0].Metadata.GetProperty("origin").GetString());
        await Assert.ThrowsAsync<SearchCursorExpiredException>(async () => await fixture.Store.ReadPageAsync(new SearchScope("other", "knowledge", ["reader"]), hybrid));
    });

    [Fact]
    public Task OpenSearch_competing_versions_replace_all_chunks_and_durable_tombstones_ignore_delayed_writes() => WithFixtureAsync(async fixture =>
    {
        var original = new SearchDocument("tenant", "knowledge", "1", 1, "cat", "cat one", true);
        Assert.Equal(SearchIngestionStatus.Applied, (await fixture.Store.UpsertAsync(original)).Status);
        Assert.Equal(SearchIngestionStatus.AlreadyApplied, (await fixture.Store.UpsertAsync(original)).Status);
        await Assert.ThrowsAsync<SearchVersionConflictException>(async () => await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "1", 1, "different", "different", true)));
        var competing = Enumerable.Range(2, 4).Select(version => fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "1", version, "dog", "dog final version " + version, true)).AsTask()).ToArray();
        _ = await Task.WhenAll(competing);
        var scope = new SearchScope("tenant", "knowledge");
        var current = await fixture.Store.SearchAsync(scope, new SearchRequest { Text = "dog" });
        Assert.Equal(5, Assert.Single((await fixture.Store.ReadPageAsync(scope, current)).Hits).SourceVersion);
        var removed = await fixture.Store.DeleteAsync("tenant", "knowledge", "1", 6);
        Assert.Equal(SearchIngestionStatus.Applied, removed.Status);
        Assert.Equal(SearchIngestionStatus.AlreadyApplied, (await fixture.Store.DeleteAsync("tenant", "knowledge", "1", 6)).Status);
        Assert.Equal(SearchIngestionStatus.StaleIgnored, (await fixture.Store.UpsertAsync(original)).Status);
        var empty = await fixture.Store.SearchAsync(scope, new SearchRequest { Text = "dog" });
        Assert.Empty((await fixture.Store.ReadPageAsync(scope, empty)).Hits);
    });

    [Fact]
    public Task Rank_snapshots_are_stable_but_pages_recheck_revocation_versions_and_byte_limits() => WithFixtureAsync(async fixture =>
    {
        for (var i = 0; i < 4; i++)
        {
            _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "document-" + i, 1, "cat", "cat " + new string('x', 40), principals: ["reader"]));
        }
        var scope = new SearchScope("tenant", "knowledge", ["reader"]);
        var ranked = await fixture.Store.SearchAsync(scope, new SearchRequest { Text = "cat" });
        var first = await fixture.Store.ReadPageAsync(scope, ranked, 1);
        Assert.Equal("document-0", Assert.Single(first.Hits).DocumentId);
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "document-1", 2, "cat", "cat revoked", principals: ["owner"]));
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "document-new", 1, "cat", "cat later", true));
        var rest = await fixture.Store.ReadPageAsync(scope, ranked, 10, first.NextAfterRank!.Value);
        Assert.Equal(["document-2", "document-3"], rest.Hits.Select(static hit => hit.DocumentId));
        Assert.Null(rest.NextAfterRank);
        await using var bounded = new OpenSearchStore(fixture.Http, fixture.Store.Options with { Limits = fixture.Store.Options.Limits with { MaxPageBytes = 90 } }, fixture.Embeddings);
        await bounded.InitializeAsync();
        var boundedSession = await bounded.SearchAsync(scope, new SearchRequest { Text = "cat" });
        Assert.Single((await bounded.ReadPageAsync(scope, boundedSession, 10)).Hits);
    });

    [Fact]
    public Task OpenSearch_embedding_failure_and_changed_model_contract_preserve_prior_content() => WithFixtureAsync(async fixture =>
    {
        _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "1", 1, "cat", "cat initial", true));
        fixture.Embeddings.Invalid = true;
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", "1", 2, "dog", "dog broken", true)));
        fixture.Embeddings.Invalid = false;
        var scope = new SearchScope("tenant", "knowledge");
        var ranked = await fixture.Store.SearchAsync(scope, new SearchRequest { Text = "cat" });
        Assert.Equal(1, Assert.Single((await fixture.Store.ReadPageAsync(scope, ranked)).Hits).SourceVersion);
        await using var incompatible = new OpenSearchStore(fixture.Http, fixture.Store.Options with { VectorDimensions = 2 }, fixture.Embeddings);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await incompatible.InitializeAsync());
        await fixture.Store.InitializeAsync();
    });

    [Fact]
    public async Task HTTP_response_payloads_are_bounded_and_borrowed_client_remains_usable()
    {
        using var client = new HttpClient(new OversizedHandler());
        var options = new OpenSearchStoreOptions { Endpoint = new Uri("http://localhost/"), IndexName = "test", MaxResponseBytes = 1024 };
        await using var store = new OpenSearchStore(client, options);
        await Assert.ThrowsAsync<OpenSearchResponseException>(async () => await store.InitializeAsync());
        await store.DisposeAsync();
        using var response = await client.GetAsync(new Uri("http://localhost/"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await store.SearchAsync(new SearchScope("tenant", "index"), new SearchRequest { Text = "cat" }));
    }

    [Fact]
    public async Task HTTP_cancellation_releases_nonqueued_admission_and_owned_client_disposal_is_explicit()
    {
        var handler = new BlockingHandler();
        using var client = new HttpClient(handler);
        await using var store = new OpenSearchStore(client, new OpenSearchStoreOptions { Endpoint = new Uri("http://localhost/"), IndexName = "test", MaxConcurrentOperations = 1 }, ownership: SearchHttpClientOwnership.Owned);
        using var cancellation = new CancellationTokenSource();
        var first = store.InitializeAsync(cancellation.Token).AsTask();
        await handler.Started.Task;
        await Assert.ThrowsAsync<SearchBackpressureException>(async () => await store.InitializeAsync());
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.UpsertAsync(new SearchDocument("tenant", "index", "1", 1, "title", "content")));
        await store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await client.GetAsync(new Uri("http://localhost/")));
    }

    [Fact]
    public Task OpenSearch_bounded_workload_pages_all_ranked_parents_once() => WithFixtureAsync(async fixture =>
    {
        const int count = 32;
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, token) =>
        {
            _ = await fixture.Store.UpsertAsync(new SearchDocument("tenant", "knowledge", i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), 1, "cat workload", "cat " + new string('x', 1024), true), token);
        });
        var scope = new SearchScope("tenant", "knowledge");
        var ranked = await fixture.Store.SearchAsync(scope, new SearchRequest { Mode = SearchMode.Hybrid, Text = "cat", Vector = new float[] { 1, 0, 0 }, CandidateLimit = 64 });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? after = 0;
        do
        {
            var page = await fixture.Store.ReadPageAsync(scope, ranked, 7, after.Value);
            Assert.All(page.Hits, hit => Assert.True(seen.Add(hit.DocumentId)));
            after = page.NextAfterRank;
        } while (after is not null);
        Assert.Equal(count, seen.Count);
    });

    private static async Task WithFixtureAsync(Func<Fixture, Task> test)
    {
        var endpoint = Environment.GetEnvironmentVariable("BLUETUSK_SEARCH_OPENSEARCH_ENDPOINT")
            ?? throw new InvalidOperationException("Set BLUETUSK_SEARCH_OPENSEARCH_ENDPOINT to a disposable OpenSearch 3.x fixture.");
        var index = "bluetusk-search-test-" + Guid.NewGuid().ToString("N");
        using var http = new HttpClient();
        var embeddings = new Embeddings();
        await using var store = new OpenSearchStore(http, new OpenSearchStoreOptions
        {
            Endpoint = new Uri(endpoint), IndexName = index, Replicas = 0, VectorDimensions = 3,
            Limits = new SearchStoreOptions { MaxChunkCharacters = 128, ChunkOverlapCharacters = 8 },
        }, embeddings);
        try
        {
            await store.InitializeAsync();
            await test(new(http, store, embeddings));
        }
        finally
        {
            using var response = await http.DeleteAsync(new Uri(endpoint.TrimEnd('/') + "/" + index));
            response.EnsureSuccessStatusCode();
        }
    }
    private static async Task WithCursorStoreAsync(Func<BlueTuskDataSource, PostgreSqlOpenSearchCursorStore, Task> test, OpenSearchCursorStoreOptions? options = null)
    {
        var connection = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") ?? throw new InvalidOperationException("Configure disposable PostgreSQL cursor checkpoint storage.");
        await using var source = BlueTuskDataSource.Create(connection);
        var schema = "opensearch_cursor_" + Guid.NewGuid().ToString("N");
        await using var cursors = new PostgreSqlOpenSearchCursorStore(source, (options ?? new()) with { Schema = schema });
        try { await cursors.InitializeAsync(); await test(source, cursors); }
        finally { await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"); _ = await cleanup.ExecuteNonQueryAsync(); }
    }
    private sealed record Fixture(HttpClient Http, OpenSearchStore Store, Embeddings Embeddings);
    private sealed class Embeddings : ISearchEmbeddingProvider
    {
        public string ModelIdentity => "opensearch-test-v1";
        internal bool Invalid { get; set; }
        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(texts.Select(text => (ReadOnlyMemory<float>)(Invalid ? new float[] { float.NaN, 0, 0 } : text.Contains("cat", StringComparison.Ordinal) ? new float[] { 1, 0, 0 } : new float[] { 0, 1, 0 })).ToArray());
        }
    }
    private sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2048), Encoding.UTF8, "application/json") });
    }
    private sealed class BlockingHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

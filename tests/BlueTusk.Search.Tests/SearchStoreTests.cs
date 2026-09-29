using System.Data.Common;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Search.PgVector;
using Xunit.Sdk;

namespace BlueTusk.Search.Tests;

public sealed class SearchStoreTests
{
    [Fact]
    public void Chunking_is_bounded_deterministic_and_preserves_surrogate_pairs()
    {
        var chunks = SearchChunker.Chunk("ab😀cd😀ef", 4, 1, 10);
        Assert.Equal(chunks.Select(static x => x.Content), SearchChunker.Chunk("ab😀cd😀ef", 4, 1, 10).Select(static x => x.Content));
        Assert.All(chunks, static chunk =>
        {
            Assert.False(char.IsLowSurrogate(chunk.Content[0]));
            Assert.False(char.IsHighSurrogate(chunk.Content[^1]));
            Assert.InRange(chunk.Content.Length, 1, 4);
        });
        Assert.Single(SearchChunker.Chunk(string.Empty));
        Assert.Throws<ArgumentException>(() => SearchChunker.Chunk(new string('a', 20), 4, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SearchChunker.Chunk("x", 1));
    }

    [Fact]
    public void Scope_inputs_are_snapshotted_and_vectors_validate_dimensions_norm_and_finiteness()
    {
        string[] principals = ["readers", "admin", "readers"];
        var scope = new SearchScope("tenant", "index", principals);
        principals[0] = "changed";
        Assert.Equal(["admin", "readers"], scope.Principals);
        Assert.Throws<ArgumentException>(() => new SearchScope("bad\0tenant", "index"));
        var adapter = new PgVectorSearchAdapter(3);
        Assert.Equal("[1,0,0]", adapter.Encode([1, 0, 0]));
        Assert.Throws<ArgumentException>(() => adapter.Encode([1, 0]));
        Assert.Throws<ArgumentException>(() => adapter.Encode([0, 0, 0]));
        Assert.Throws<ArgumentException>(() => adapter.Encode([1, float.NaN, 0]));
    }

    [Fact]
    public Task Fulltext_search_enforces_tenant_index_permissions_and_public_access() => WithStoreAsync(async (store, unusedSource) =>
    {
        await store.UpsertAsync(Doc("public", 1, "cat animal", isPublic: true));
        await store.UpsertAsync(Doc("allowed", 1, "cat animal", principals: ["readers"]));
        await store.UpsertAsync(Doc("private", 1, "cat animal", principals: ["admins"]));
        await store.UpsertAsync(new SearchDocument("other", "library", "other-tenant", 1, "cat", "cat animal", true));
        await store.UpsertAsync(new SearchDocument("tenant", "other-index", "other-index", 1, "cat", "cat animal", true));
        var publicPage = await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" });
        Assert.Equal("public", Assert.Single(publicPage.Hits).DocumentId);
        var readers = await store.SearchAsync(new SearchScope("tenant", "library", ["readers"]), new SearchRequest { Text = "cat" });
        Assert.Equal(["allowed", "public"], readers.Hits.Select(static x => x.DocumentId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(readers.Hits, static x => x.DocumentId is "private" or "other-index" or "other-tenant");
    });

    [Fact]
    public Task Version_fences_handle_duplicates_conflicting_replays_and_deletion_resurrection() => WithStoreAsync(async (store, unusedSource) =>
    {
        var document = Doc("1", 2, "cat", true);
        Assert.Equal(SearchIngestionStatus.Applied, (await store.UpsertAsync(document)).Status);
        Assert.Equal(SearchIngestionStatus.AlreadyApplied, (await store.UpsertAsync(document)).Status);
        Assert.Equal(SearchIngestionStatus.StaleIgnored, (await store.UpsertAsync(Doc("1", 1, "dog", true))).Status);
        _ = await Assert.ThrowsAsync<SearchVersionConflictException>(() => store.UpsertAsync(Doc("1", 2, "conflicting", true)).AsTask());
        Assert.Equal(SearchIngestionStatus.Applied, (await store.DeleteAsync("tenant", "library", "1", 3)).Status);
        Assert.Equal(SearchIngestionStatus.StaleIgnored, (await store.UpsertAsync(document)).Status);
        Assert.Empty((await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" })).Hits);
        Assert.Equal(SearchIngestionStatus.AlreadyApplied, (await store.DeleteAsync("tenant", "library", "1", 3)).Status);
        Assert.Equal(SearchIngestionStatus.Applied, (await store.UpsertAsync(Doc("1", 4, "cat returned", true))).Status);
        Assert.Single((await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" })).Hits);
    });

    [Fact]
    public Task Competing_ingestion_versions_never_leave_chunks_at_an_older_fence() => WithStoreAsync(async (store, source) =>
    {
        await Task.WhenAll(Enumerable.Range(1, 8).Select(version => store.UpsertAsync(Doc("race", version, $"cat version {version}", true)).AsTask()));
        var page = await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" });
        Assert.Equal(8, Assert.Single(page.Hits).SourceVersion);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{store.Options.Schema}\".chunks c JOIN \"{store.Options.Schema}\".documents d USING (tenant,index_name,document_id) WHERE c.source_version<>d.source_version";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    });

    [Fact]
    public Task Stable_rank_snapshots_ignore_new_documents_and_recheck_revocations_and_scope() => WithStoreAsync(async (store, unusedSource) =>
    {
        for (var i = 0; i < 5; i++)
        {
            await store.UpsertAsync(Doc($"{i:D3}", 1, "cat animal", principals: ["readers"]));
        }

        var scope = new SearchScope("tenant", "library", ["readers"]);
        var first = await store.SearchAsync(scope, new SearchRequest { Text = "cat", PageSize = 2 });
        Assert.Equal(["000", "001"], first.Hits.Select(static x => x.DocumentId));
        Assert.NotNull(first.NextCursor);
        await store.UpsertAsync(Doc("new", 1, "cat animal", principals: ["readers"]));
        await store.UpsertAsync(Doc("002", 2, "cat animal", principals: ["admins"]));
        _ = await Assert.ThrowsAsync<SearchCursorExpiredException>(() => store.ContinueSearchAsync(new SearchScope("tenant", "library", ["admins"]), first.NextCursor!).AsTask());
        _ = await Assert.ThrowsAsync<SearchCursorExpiredException>(() => store.ContinueSearchAsync(new SearchScope("other", "library", ["readers"]), first.NextCursor!).AsTask());
        var second = await store.ContinueSearchAsync(scope, first.NextCursor!, 2);
        Assert.Equal(["003", "004"], second.Hits.Select(static x => x.DocumentId));
        Assert.Null(second.NextCursor);
    });

    [Fact]
    public Task Expired_query_cleanup_and_query_capacity_are_bounded() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("1", 1, "cat", true));
        var first = await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" });
        _ = await Assert.ThrowsAsync<SearchBackpressureException>(() => store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" }).AsTask());
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE \"{store.Options.Schema}\".queries SET expires_at=clock_timestamp()-interval '1 second'";
        _ = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, await store.PruneExpiredQueriesAsync(1));
        Assert.Equal(0, await store.PruneExpiredQueriesAsync(1));
        Assert.Single((await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" })).Hits);
        Assert.True(first.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }, options: new SearchStoreOptions { MaxActiveQueriesPerScope = 1 });

    [Fact]
    public Task Chunk_replacement_removes_old_terms_and_metadata_is_stored_once_per_document() => WithStoreAsync(async (store, source) =>
    {
        using var metadata = JsonDocument.Parse("{\"source\":\"fixture\"}");
        var document = new SearchDocument("tenant", "library", "chunks", 1, "", "cat " + new string('x', 60), true, metadata: metadata.RootElement);
        var result = await store.UpsertAsync(document);
        Assert.True(result.ChunkCount > 1);
        await store.UpsertAsync(Doc("chunks", 2, "dog", true));
        Assert.Empty((await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" })).Hits);
        Assert.Single((await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "dog" })).Hits);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{store.Options.Schema}\".chunks WHERE document_id='chunks'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }, options: new SearchStoreOptions { MaxChunkCharacters = 16, ChunkOverlapCharacters = 2 });

    [Fact]
    public Task Vector_and_hybrid_search_rank_scoped_candidates_and_create_optional_hnsw_index() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("cat", 1, "cat animal", true));
        await store.UpsertAsync(Doc("dog", 1, "dog animal", true));
        await store.UpsertAsync(Doc("private-cat", 1, "cat animal", principals: ["admin"]));
        await store.UpsertAsync(new SearchDocument("other", "library", "other-cat", 1, "", "cat animal", true));
        var scope = new SearchScope("tenant", "library");
        var vector = await store.SearchAsync(scope, new SearchRequest { Mode = SearchMode.Vector, Vector = new float[] { 1, 0, 0 }, PageSize = 2 });
        Assert.Equal(["cat", "dog"], vector.Hits.Select(static x => x.DocumentId));
        Assert.True(vector.Hits[0].Score > vector.Hits[1].Score);
        var hybrid = await store.SearchAsync(scope, new SearchRequest { Mode = SearchMode.Hybrid, Text = "cat", PageSize = 2 });
        Assert.Equal("cat", hybrid.Hits[0].DocumentId);
        Assert.DoesNotContain(hybrid.Hits, static x => x.DocumentId is "private-cat" or "other-cat");
        var adapter = new PgVectorSearchAdapter(3);
        await adapter.CreateHnswIndexAsync(source, store.Options.Schema);
        var exactAfterIndex = await store.SearchAsync(scope, new SearchRequest { Mode = SearchMode.Vector, Vector = new float[] { 1, 0, 0 }, PageSize = 2 });
        Assert.Equal(vector.Hits.Select(static x => x.DocumentId), exactAfterIndex.Hits.Select(static x => x.DocumentId));
    }, useVectors: true);

    [Fact]
    public Task Embedding_failure_does_not_advance_version_or_replace_searchable_chunks() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("1", 1, "cat", true));
        await using var failing = new PostgreSqlSearchStore(source, store.Options, new PgVectorSearchAdapter(3), new InvalidEmbeddings());
        _ = await Assert.ThrowsAsync<ArgumentException>(() => failing.UpsertAsync(Doc("1", 2, "dog", true)).AsTask());
        var page = await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" });
        Assert.Equal(1, Assert.Single(page.Hits).SourceVersion);
    }, useVectors: true);

    [Fact]
    public Task Concurrent_embedding_admission_rejects_overload_without_unbounded_waiters() => WithStoreAsync(async (store, source) =>
    {
        var blocking = new BlockingEmbeddings();
        await using var bounded = new PostgreSqlSearchStore(source, store.Options with { MaxConcurrentIngestions = 1 }, new PgVectorSearchAdapter(3), blocking);
        var first = bounded.UpsertAsync(Doc("1", 1, "cat", true)).AsTask();
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _ = await Assert.ThrowsAsync<SearchBackpressureException>(() => bounded.UpsertAsync(Doc("2", 1, "cat", true)).AsTask());
        blocking.Release.TrySetResult();
        Assert.Equal(SearchIngestionStatus.Applied, (await first).Status);
    }, useVectors: true);

    [Fact]
    public Task Chunk_failure_rolls_back_parent_fence_and_preserves_previous_search_results() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("1", 1, "cat", true));
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE FUNCTION "{store.Options.Schema}".reject_chunks() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.ordinal>0 THEN RAISE EXCEPTION 'injected chunk failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_chunks BEFORE INSERT ON "{store.Options.Schema}".chunks
            FOR EACH ROW EXECUTE FUNCTION "{store.Options.Schema}".reject_chunks();
            """;
        _ = await command.ExecuteNonQueryAsync();
        _ = await Assert.ThrowsAsync<BlueTuskException>(() => store.UpsertAsync(Doc("1", 2, "dog " + new string('x', 60), true)).AsTask());
        Assert.Equal(1, Assert.Single((await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" })).Hits).SourceVersion);
        Assert.Empty((await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "dog" })).Hits);
    }, options: new SearchStoreOptions { MaxChunkCharacters = 16, ChunkOverlapCharacters = 2 });

    [Fact]
    public Task Ranking_extensions_reorder_only_authorized_candidates_and_cannot_inject_results() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("a", 1, "cat", true));
        await store.UpsertAsync(Doc("z", 1, "cat", true));
        await store.UpsertAsync(Doc("private", 1, "cat", principals: ["admin"]));
        await using var custom = new PostgreSqlSearchStore(source, store.Options, ranking: new ReverseRanking());
        var page = await custom.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat", PageSize = 1 });
        Assert.Equal("z", Assert.Single(page.Hits).DocumentId);
        Assert.Equal("a", Assert.Single((await custom.ContinueSearchAsync(new SearchScope("tenant", "library"), page.NextCursor!, 1)).Hits).DocumentId);
        await using var invalid = new PostgreSqlSearchStore(source, store.Options, ranking: new InjectedRanking());
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" }).AsTask());
    });

    [Fact]
    public Task Pending_ranking_does_not_hold_scope_admission_and_cannot_be_paged() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("1", 1, "cat", true));
        var ranker = new BarrierRanking();
        await using var ranked = new PostgreSqlSearchStore(source, store.Options with { MaxActiveQueriesPerScope = 2, MaxConcurrentRankings = 2 }, ranking: ranker);
        var scope = new SearchScope("tenant", "library");
        var first = ranked.SearchAsync(scope, new SearchRequest { Text = "cat" }).AsTask();
        await ranker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT query_id FROM \"{store.Options.Schema}\".queries WHERE NOT ready";
            var pendingId = Assert.IsType<Guid>(await command.ExecuteScalarAsync());
            _ = await Assert.ThrowsAsync<SearchCursorExpiredException>(() => ranked.ContinueSearchAsync(scope, new SearchCursor(pendingId, 0)).AsTask());
            command.CommandText = $"UPDATE \"{store.Options.Schema}\".queries SET expires_at=clock_timestamp()+interval '20 seconds' WHERE query_id=@query";
            var query = command.CreateParameter(); query.ParameterName = "query"; query.Value = pendingId; command.Parameters.Add(query);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            var second = await ranked.SearchAsync(scope, new SearchRequest { Text = "cat" }).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("1", Assert.Single(second.Hits).DocumentId);
            _ = await Assert.ThrowsAsync<SearchBackpressureException>(() => ranked.SearchAsync(scope, new SearchRequest { Text = "cat" }).AsTask());
        }
        finally
        {
            ranker.Release.TrySetResult();
        }

        var firstPage = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("1", Assert.Single(firstPage.Hits).DocumentId);
        Assert.True(firstPage.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1));
    });

    [Fact]
    public Task Failed_ranking_and_candidate_insert_release_pending_capacity() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("1", 1, "cat", true));
        var options = store.Options with { MaxActiveQueriesPerScope = 1 };
        var scope = new SearchScope("tenant", "library");
        await using (var invalid = new PostgreSqlSearchStore(source, options, ranking: new InjectedRanking()))
        {
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.SearchAsync(scope, new SearchRequest { Text = "cat" }).AsTask());
        }

        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{store.Options.Schema}\".queries";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        command.CommandText = $"""
            CREATE FUNCTION "{store.Options.Schema}".reject_rank() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected rank failure'; END $$;
            CREATE TRIGGER fail_rank BEFORE INSERT ON "{store.Options.Schema}".query_results
            FOR EACH ROW EXECUTE FUNCTION "{store.Options.Schema}".reject_rank();
            """;
        _ = await command.ExecuteNonQueryAsync();
        _ = await Assert.ThrowsAsync<BlueTuskException>(() => store.SearchAsync(scope, new SearchRequest { Text = "cat" }).AsTask());
        command.CommandText = $"SELECT count(*) FROM \"{store.Options.Schema}\".queries";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        command.CommandText = $"SELECT reserved_queries,reserved_rows,reserved_bytes FROM \"{store.Options.Schema}\".snapshot_budget";
        await using (var budget = await command.ExecuteReaderAsync())
        {
            Assert.True(await budget.ReadAsync());
            Assert.Equal(0L, budget.GetInt64(0));
            Assert.Equal(0L, budget.GetInt64(1));
            Assert.Equal(0L, budget.GetInt64(2));
        }
        command.CommandText = $"DROP TRIGGER fail_rank ON \"{store.Options.Schema}\".query_results";
        _ = await command.ExecuteNonQueryAsync();
        await using var bounded = new PostgreSqlSearchStore(source, options);
        Assert.Equal("1", Assert.Single((await bounded.SearchAsync(scope, new SearchRequest { Text = "cat" })).Hits).DocumentId);
    });

    [Fact]
    public Task Expired_pending_ranking_cannot_publish_and_is_pruned() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("1", 1, "cat", true));
        var ranker = new BarrierRanking();
        await using var ranked = new PostgreSqlSearchStore(source, store.Options with { MaxActiveQueriesPerScope = 1 }, ranking: ranker);
        var first = ranked.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" }).AsTask();
        await ranker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE \"{store.Options.Schema}\".queries SET expires_at=clock_timestamp()-interval '1 second' WHERE NOT ready";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            Assert.Equal(1, await ranked.PruneExpiredQueriesAsync(1));
        }
        finally
        {
            ranker.Release.TrySetResult();
        }

        _ = await Assert.ThrowsAsync<SearchCursorExpiredException>(() => first);
        Assert.Single((await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" })).Hits);
    });

    [Fact]
    public Task Revocation_during_ranking_is_rechecked_before_delivery() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("1", 1, "cat", principals: ["readers"]));
        var ranker = new BarrierRanking();
        await using var ranked = new PostgreSqlSearchStore(source, store.Options, ranking: ranker);
        var scope = new SearchScope("tenant", "library", ["readers"]);
        var first = ranked.SearchAsync(scope, new SearchRequest { Text = "cat" }).AsTask();
        await ranker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await store.UpsertAsync(Doc("1", 2, "cat", principals: ["admins"]));
        }
        finally
        {
            ranker.Release.TrySetResult();
        }

        Assert.Empty((await first.WaitAsync(TimeSpan.FromSeconds(10))).Hits);
    });

    [Fact]
    public Task Ranking_admission_rejects_overload_without_adding_pending_rows() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("1", 1, "cat", true));
        var ranker = new BarrierRanking();
        await using var ranked = new PostgreSqlSearchStore(source, store.Options with { MaxConcurrentRankings = 1 }, ranking: ranker);
        var scope = new SearchScope("tenant", "library");
        var first = ranked.SearchAsync(scope, new SearchRequest { Text = "cat" }).AsTask();
        await ranker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            _ = await Assert.ThrowsAsync<SearchBackpressureException>(() => ranked.SearchAsync(scope, new SearchRequest { Text = "cat" }).AsTask());
            await using var connection = await source.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM \"{store.Options.Schema}\".queries WHERE NOT ready";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        finally
        {
            ranker.Release.TrySetResult();
        }

        Assert.Single((await first.WaitAsync(TimeSpan.FromSeconds(10))).Hits);
    });

    [Fact]
    public Task Cancelled_ranking_releases_its_reservation_and_local_slot() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("1", 1, "cat", true));
        var ranker = new BarrierRanking();
        await using var ranked = new PostgreSqlSearchStore(source,
            store.Options with { MaxActiveQueriesPerScope = 1, MaxConcurrentRankings = 1 }, ranking: ranker);
        var scope = new SearchScope("tenant", "library");
        using var cancellation = new CancellationTokenSource();
        var first = ranked.SearchAsync(scope, new SearchRequest { Text = "cat" }, cancellation.Token).AsTask();
        await ranker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{store.Options.Schema}\".queries";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        Assert.Single((await ranked.SearchAsync(scope, new SearchRequest { Text = "cat" })).Hits);
    });

    [Fact]
    public Task Version_one_rank_snapshots_upgrade_as_ready_without_losing_cursor() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("1", 1, "cat", true));
        await store.UpsertAsync(Doc("2", 1, "cat", true));
        var scope = new SearchScope("tenant", "library");
        var first = await store.SearchAsync(scope, new SearchRequest { Text = "cat", PageSize = 1 });
        Assert.NotNull(first.NextCursor);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE \"{store.Options.Schema}\".snapshot_budget; ALTER TABLE \"{store.Options.Schema}\".queries DROP COLUMN reserved_rows, DROP COLUMN reserved_bytes, DROP COLUMN ready; UPDATE \"{store.Options.Schema}\".storage_metadata SET storage_version=1";
        _ = await command.ExecuteNonQueryAsync();
        await using var upgraded = new PostgreSqlSearchStore(source, store.Options);
        await upgraded.InitializeAsync();
        Assert.Equal("2", Assert.Single((await upgraded.ContinueSearchAsync(scope, first.NextCursor!, 1)).Hits).DocumentId);
        command.CommandText = $"SELECT count(*) FROM \"{store.Options.Schema}\".queries WHERE ready";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    });

    [Fact]
    public Task Version_two_rank_snapshots_upgrade_with_accounted_rows_and_existing_cursor() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("1", 1, "cat", true));
        await store.UpsertAsync(Doc("2", 1, "cat", true));
        var scope = new SearchScope("tenant", "library");
        var first = await store.SearchAsync(scope, new SearchRequest { Text = "cat", PageSize = 1 });
        Assert.NotNull(first.NextCursor);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE \"{store.Options.Schema}\".snapshot_budget; ALTER TABLE \"{store.Options.Schema}\".queries DROP COLUMN reserved_rows, DROP COLUMN reserved_bytes; UPDATE \"{store.Options.Schema}\".storage_metadata SET storage_version=2";
        _ = await command.ExecuteNonQueryAsync();
        await using var upgraded = new PostgreSqlSearchStore(source, store.Options);
        await upgraded.InitializeAsync();
        Assert.Equal("2", Assert.Single((await upgraded.ContinueSearchAsync(scope, first.NextCursor!, 1)).Hits).DocumentId);
        command.CommandText = $"SELECT reserved_queries,reserved_rows,reserved_bytes FROM \"{store.Options.Schema}\".snapshot_budget";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.True(reader.GetInt64(2) >= 80);
    });

    [Fact]
    public Task Deployment_wide_rank_budget_spans_tenants_and_store_instances_and_releases_expiry() => WithStoreAsync(async (store, source) =>
    {
        var options = store.Options;
        await using var second = new PostgreSqlSearchStore(source, options);
        await second.InitializeAsync();
        var request = new SearchRequest { Text = "cat", CandidateLimit = 1, PageSize = 1 };
        var first = new SearchScope("tenant-a", "library");
        var other = new SearchScope("tenant-b", "library");
        _ = await store.SearchAsync(first, request);
        _ = await second.SearchAsync(other, request);
        _ = await Assert.ThrowsAsync<SearchBackpressureException>(() => store.SearchAsync(new SearchScope("tenant-c", "library"), request).AsTask());

        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT reserved_queries,reserved_rows,reserved_bytes FROM \"{options.Schema}\".snapshot_budget";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.Equal(1104L, reader.GetInt64(2));
        }

        command.CommandText = $"UPDATE \"{options.Schema}\".queries SET expires_at=clock_timestamp()-interval '1 second' WHERE tenant='tenant-a'";
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        Assert.Equal(1, await second.PruneExpiredQueriesAsync(1));
        _ = await store.SearchAsync(new SearchScope("tenant-c", "library"), request);
        command.CommandText = $"SELECT reserved_queries,reserved_rows,reserved_bytes FROM \"{options.Schema}\".snapshot_budget";
        await using var after = await command.ExecuteReaderAsync();
        Assert.True(await after.ReadAsync());
        Assert.Equal(2L, after.GetInt64(0));
        Assert.Equal(2L, after.GetInt64(1));
        Assert.Equal(1104L, after.GetInt64(2));
    }, options: new SearchStoreOptions { MaxCandidateCount = 1, MaxPageSize = 1, MaxRetainedQueries = 2, MaxRetainedRankRows = 3, MaxRetainedRankBytes = 1656 });

    [Fact]
    public Task Rank_row_budget_rejects_across_scopes_and_failure_rolls_back_reservation() => WithStoreAsync(async (store, source) =>
    {
        await store.UpsertAsync(Doc("one", 1, "cat", true));
        var request = new SearchRequest { Text = "cat", CandidateLimit = 1, PageSize = 1 };
        await using (var invalid = new PostgreSqlSearchStore(source, store.Options, ranking: new InjectedRanking()))
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.SearchAsync(new SearchScope("tenant", "library"), request).AsTask());

        var other = new SearchScope("another", "library");
        _ = await store.SearchAsync(other, request);
        _ = await Assert.ThrowsAsync<SearchBackpressureException>(() => store.SearchAsync(new SearchScope("third", "library"), request).AsTask());
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT reserved_queries,reserved_rows,reserved_bytes FROM \"{store.Options.Schema}\".snapshot_budget";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(552L, reader.GetInt64(2));
    }, options: new SearchStoreOptions { MaxCandidateCount = 1, MaxPageSize = 1, MaxRetainedQueries = 4, MaxRetainedRankRows = 1, MaxRetainedRankBytes = 1104 });

    [Fact]
    public Task Rank_byte_budget_rejects_second_scope_even_with_row_headroom() => WithStoreAsync(async (store, unusedSource) =>
    {
        var request = new SearchRequest { Text = "cat", CandidateLimit = 1, PageSize = 1 };
        _ = await store.SearchAsync(new SearchScope("first", "library"), request);
        _ = await Assert.ThrowsAsync<SearchBackpressureException>(() => store.SearchAsync(new SearchScope("second", "library"), request).AsTask());
    }, options: new SearchStoreOptions { MaxCandidateCount = 1, MaxPageSize = 1, MaxRetainedQueries = 4, MaxRetainedRankRows = 2, MaxRetainedRankBytes = 552 });

    [Fact]
    public Task Concurrent_tenants_cannot_overbook_shared_rank_budget() => WithStoreAsync(async (store, source) =>
    {
        await using var other = new PostgreSqlSearchStore(source, store.Options);
        var request = new SearchRequest { Text = "cat", CandidateLimit = 1, PageSize = 1 };
        await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
        {
            try
            {
                _ = await (i % 2 == 0 ? store : other).SearchAsync(new SearchScope($"tenant-{i}", "library"), request);
            }
            catch (SearchBackpressureException)
            {
                // A nonwaiting global reservation is allowed to shed simultaneous arrivals.
            }
        }));

        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT b.reserved_queries,b.reserved_rows,b.reserved_bytes,(SELECT count(*) FROM \"{store.Options.Schema}\".queries) FROM \"{store.Options.Schema}\".snapshot_budget b";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var retained = reader.GetInt64(0);
        Assert.InRange(retained, 1L, 4L);
        Assert.Equal(retained, reader.GetInt64(1));
        Assert.Equal(retained * 552, reader.GetInt64(2));
        Assert.Equal(retained, reader.GetInt64(3));
    }, options: new SearchStoreOptions { MaxCandidateCount = 1, MaxPageSize = 1, MaxRetainedQueries = 4, MaxRetainedRankRows = 4, MaxRetainedRankBytes = 2208 });

    [Fact]
    public Task Short_global_budget_contention_waits_without_shedding_queries() => WithStoreAsync(async (store, source) =>
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@name, 0))";
        var name = command.CreateParameter();
        name.ParameterName = "name";
        name.Value = "BlueTusk.Search.SnapshotBudget:" + store.Options.Schema;
        command.Parameters.Add(name);
        _ = await command.ExecuteScalarAsync();

        var arrivals = Enumerable.Range(0, 4).Select(index =>
            store.SearchAsync(new SearchScope($"tenant-{index}", "library"),
                new SearchRequest { Text = "cat", CandidateLimit = 1, PageSize = 1 }).AsTask()).ToArray();
        await Task.Delay(80);
        await transaction.CommitAsync();
        await Task.WhenAll(arrivals);
    });

    [Fact]
    public Task Page_and_ranking_byte_budgets_stop_payloads_before_they_leave_postgresql() => WithStoreAsync(async (store, source) =>
    {
        for (var i = 0; i < 3; i++)
        {
            await store.UpsertAsync(Doc($"{i:D3}", 1, "cat " + new string('x', 40), true));
        }

        var scope = new SearchScope("tenant", "library");
        var page = await store.SearchAsync(scope, new SearchRequest { Text = "cat", PageSize = 3 });
        Assert.Equal("000", Assert.Single(page.Hits).DocumentId);
        page = await store.ContinueSearchAsync(scope, page.NextCursor!, 3);
        Assert.Equal("001", Assert.Single(page.Hits).DocumentId);
        page = await store.ContinueSearchAsync(scope, page.NextCursor!, 3);
        Assert.Equal("002", Assert.Single(page.Hits).DocumentId);
        Assert.Null(page.NextCursor);
        await using var boundedRank = new PostgreSqlSearchStore(source, store.Options with { MaxRerankingBytes = 10 }, ranking: new ReverseRanking());
        _ = await Assert.ThrowsAsync<SearchBackpressureException>(() => boundedRank.SearchAsync(scope, new SearchRequest { Text = "cat" }).AsTask());
    }, options: new SearchStoreOptions { MaxPageBytes = 65 });

    [Fact]
    public Task Bounded_concurrent_workload_indexes_512_documents_and_pages_all_ranked_results() => WithStoreAsync(async (store, unusedSource) =>
    {
        const int workers = 8;
        const int documentsPerWorker = 64;
        await Task.WhenAll(Enumerable.Range(0, workers).Select(async worker =>
        {
            for (var i = 0; i < documentsPerWorker; i++)
            {
                Assert.Equal(SearchIngestionStatus.Applied, (await store.UpsertAsync(Doc($"{worker:D2}-{i:D3}", 1, "cat " + new string('x', 1024), true))).Status);
            }
        }));
        var scope = new SearchScope("tenant", "library");
        var page = await store.SearchAsync(scope, new SearchRequest { Text = "cat", PageSize = 100, CandidateLimit = 1000 });
        var found = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            foreach (var hit in page.Hits)
            {
                Assert.True(found.Add(hit.DocumentId));
            }

            if (page.NextCursor is null)
            {
                break;
            }

            page = await store.ContinueSearchAsync(scope, page.NextCursor, 100);
        }

        Assert.Equal(workers * documentsPerWorker, found.Count);
    });

    [Fact]
    public Task Storage_contract_changes_and_canceled_ingestion_are_rejected() => WithStoreAsync(async (store, source) =>
    {
        await using var mismatch = new PostgreSqlSearchStore(source, store.Options with { ChunkOverlapCharacters = 1 });
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => mismatch.InitializeAsync().AsTask());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpsertAsync(Doc("1", 1, "cat", true), cancellation.Token).AsTask());
        Assert.Empty((await store.SearchAsync(new SearchScope("tenant", "library"), new SearchRequest { Text = "cat" })).Hits);
    });

    private static SearchDocument Doc(string id, long version, string text, bool isPublic = false, IReadOnlyList<string>? principals = null) =>
        new("tenant", "library", id, version, string.Empty, text, isPublic, principals);

    private static async Task WithStoreAsync(Func<PostgreSqlSearchStore, DbDataSource, Task> test, SearchStoreOptions? options = null, bool useVectors = false)
    {
        var connectionString = Environment.GetEnvironmentVariable(useVectors ? "BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING" : "BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("The requested PostgreSQL Search test connection is not configured.");
        }

        await using var source = BlueTuskDataSource.Create(connectionString);
        var schema = "search_test_" + Guid.NewGuid().ToString("N");
        await using var store = new PostgreSqlSearchStore(source, (options ?? new SearchStoreOptions()) with { Schema = schema },
            useVectors ? new PgVectorSearchAdapter(3) : null, useVectors ? new TestEmbeddings() : null);
        try
        {
            await store.InitializeAsync();
            await test(store, source);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            _ = await command.ExecuteNonQueryAsync();
        }
    }

    private class TestEmbeddings : ISearchEmbeddingProvider
    {
        public string ModelIdentity => "test:v1";
        public virtual ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ReadOnlyMemory<float>> result = texts.Select(static text => (ReadOnlyMemory<float>)(text.Contains("cat", StringComparison.Ordinal) ? new float[] { 1, 0, 0 } : text.Contains("dog", StringComparison.Ordinal) ? new float[] { 0, 1, 0 } : new float[] { 0, 0, 1 })).ToArray();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InvalidEmbeddings : TestEmbeddings
    {
        public override ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ReadOnlyMemory<float>> result = texts.Select(static text => (ReadOnlyMemory<float>)new float[] { float.NaN, 0, 0 }).ToArray();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockingEmbeddings : TestEmbeddings
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.EmbedAsync(texts, cancellationToken);
        }
    }

    private sealed class ReverseRanking : ISearchRankingExtension
    {
        public ValueTask<IReadOnlyList<SearchRankingScore>> RankAsync(SearchScope scope, string queryText, IReadOnlyList<SearchHit> candidates, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SearchRankingScore> result = candidates.Select(static hit => new SearchRankingScore(hit.DocumentId, hit.ChunkOrdinal, hit.DocumentId[0])).ToArray();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BarrierRanking : ISearchRankingExtension
    {
        private int _calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<SearchRankingScore>> RankAsync(SearchScope scope, string queryText,
            IReadOnlyList<SearchHit> candidates, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return candidates.Select(static hit => new SearchRankingScore(hit.DocumentId, hit.ChunkOrdinal, hit.Score)).ToArray();
        }
    }

    private sealed class InjectedRanking : ISearchRankingExtension
    {
        public ValueTask<IReadOnlyList<SearchRankingScore>> RankAsync(SearchScope scope, string queryText, IReadOnlyList<SearchHit> candidates, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SearchRankingScore> result = [new SearchRankingScore("private", 0, 999)];
            return ValueTask.FromResult(result);
        }
    }
}

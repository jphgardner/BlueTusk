using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Documents.Tests;

public sealed class DocumentStoreTests
{
    private static readonly DocumentCollectionDefinition<TestDocument> Collection = new("orders", TestJsonContext.Default.TestDocument, 2);
    private const string DefaultConnection = "Host=127.0.0.1;Port=55418;Username=postgres;Password=postgres;Database=bluetusk_ecosystem;SSL Mode=Disable;Channel Binding=Disable";

    [Fact]
    public async Task Sessions_validate_admission_serialization_and_duplicate_keys_without_connecting()
    {
        await using var source = BlueTuskDataSource.Create(DefaultConnection);
        await using var store = new DocumentStore(source, new DocumentStoreOptions { MaxSessionOperations = 1, MaxDocumentBytes = 100, MaxSessionBytes = 1000, MaxPageBytes = 1000 });
        using var session = store.OpenSession("tenant");
        session.Insert(Collection, "1", new TestDocument("hello", 12, null));
        Assert.Equal(1, session.PendingCount);
        Assert.Throws<InvalidOperationException>(() => session.Insert(Collection, "1", new TestDocument("same", 13, null)));
        Assert.Throws<InvalidOperationException>(() => session.Insert(Collection, "2", new TestDocument("other", 13, null)));
        session.Clear();
        Assert.Throws<ArgumentException>(() => session.Insert(Collection, "1", new TestDocument(new string('x', 1024), 12, null)));
        Assert.Equal(0, session.PendingCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Delete(Collection, "1", 0));
        Assert.Throws<ArgumentException>(() => store.OpenSession("bad\0tenant"));
        Assert.Throws<ArgumentException>(() => new DocumentCollectionDefinition<TestDocument>(new string('界', 100), TestJsonContext.Default.TestDocument));
        Assert.Throws<ArgumentException>(() => new DocumentStore(source, new DocumentStoreOptions { Schema = new string('界', 22) }));
        await store.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => session.Insert(Collection, "1", new TestDocument("hello", 12, null)));
    }

    [Fact]
    public async Task Serialization_enforces_exact_encoded_byte_limit_and_rejects_nonobject_roots()
    {
        var value = new TestDocument("界🙂\\\"\n", 17, new Customer("Ada"));
        var encodedLength = JsonSerializer.SerializeToUtf8Bytes(value, TestJsonContext.Default.TestDocument).Length;
        await using var source = BlueTuskDataSource.Create(DefaultConnection);
        await using var exactStore = new DocumentStore(source, new DocumentStoreOptions { MaxDocumentBytes = encodedLength });
        using var exact = exactStore.OpenSession("tenant");
        exact.Insert(Collection, "exact", value);
        Assert.Equal(1, exact.PendingCount);
        exact.Replace(Collection, "replacement", value, 1);
        Assert.Equal(2, exact.PendingCount);
        var numbers = new DocumentCollectionDefinition<int>("numbers", TestJsonContext.Default.Int32);
        Assert.Throws<ArgumentException>(() => exact.Insert(numbers, "scalar", 17));
        Assert.Equal(2, exact.PendingCount);
        await using var shortStore = new DocumentStore(source, new DocumentStoreOptions { MaxDocumentBytes = encodedLength - 1 });
        using var shortSession = shortStore.OpenSession("tenant");
        Assert.Throws<ArgumentException>(() => shortSession.Insert(Collection, "oversize", value));
        Assert.Equal(0, shortSession.PendingCount);
    }

    [Fact]
    public Task Staged_large_and_short_documents_retain_owned_json_after_serialization_stream_disposal() => WithStoreAsync(async (store, unusedSource) =>
    {
        var first = new TestDocument(new string('界', 32 * 1024), 12, new Customer("Ada🙂"));
        var second = new TestDocument("short\\\"\n", 13, null);
        var third = new TestDocument(new string('y', 65 * 1024), 14, new Customer("Grace"));
        using var session = store.OpenSession("tenant");
        session.Insert(Collection, "first", first);
        session.Insert(Collection, "second", second);
        session.Insert(Collection, "third", third);
        Assert.Equal(3, (await session.SaveChangesAsync()).Count);
        Assert.Equal(first, (await store.LoadAsync("tenant", Collection, "first"))!.Value);
        Assert.Equal(second, (await store.LoadAsync("tenant", Collection, "second"))!.Value);
        Assert.Equal(third, (await store.LoadAsync("tenant", Collection, "third"))!.Value);
    });

    [Fact]
    public void Patch_paths_are_snapshotted_and_validate_depth()
    {
        string[] path = ["customer", "display'name"];
        using var value = JsonDocument.Parse("\"Ada\"");
        var patch = DocumentPatch.Set(path, value.RootElement);
        path[0] = "corrupted";
        Assert.Equal("customer", patch.Path[0]);
        Assert.Equal("\"Ada\"", patch.JsonValue);
        Assert.Throws<ArgumentException>(() => DocumentPatch.Remove([]));
        Assert.Throws<ArgumentException>(() => DocumentPatch.Remove(["bad\0path"]));
        Assert.Throws<ArgumentException>(() => new DocumentIndexDefinition("lookup", DocumentIndexKind.TextPath));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentIndexDefinition("lookup", (DocumentIndexKind)99));
    }

    [Fact]
    public Task Inserts_and_replaces_roundtrip_source_generated_json_and_revisions() => WithStoreAsync(async (store, unusedSource) =>
    {
        using var session = store.OpenSession("tenant-a");
        session.Insert(Collection, "1", new TestDocument("嵐 \' ; DROP TABLE documents; --", 12, new Customer("Ada")));
        var first = Assert.Single(await session.SaveChangesAsync());
        Assert.True(first.Revision > 0);
        Assert.Equal(0, session.PendingCount);
        var loaded = Assert.IsType<StoredDocument<TestDocument>>(await store.LoadAsync("tenant-a", Collection, "1"));
        Assert.Equal("Ada", loaded.Value.Customer!.Name);
        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Equal(first.Revision, loaded.Revision);
        session.Replace(Collection, "1", loaded.Value with { Count = 99 }, loaded.Revision);
        var updated = Assert.Single(await session.SaveChangesAsync());
        Assert.True(updated.Revision > first.Revision);
        Assert.Equal(99, (await store.LoadAsync("tenant-a", Collection, "1"))!.Value.Count);
    });

    [Fact]
    public Task Competing_updates_have_one_winner_and_a_meaningful_conflict() => WithStoreAsync(async (store, unusedSource) =>
    {
        using var setup = store.OpenSession("tenant");
        setup.Insert(Collection, "race", new TestDocument("initial", 0, null));
        var revision = Assert.Single(await setup.SaveChangesAsync()).Revision!.Value;
        using var first = store.OpenSession("tenant");
        using var second = store.OpenSession("tenant");
        first.Replace(Collection, "race", new TestDocument("one", 1, null), revision);
        second.Replace(Collection, "race", new TestDocument("two", 2, null), revision);
        var outcomes = await Task.WhenAll(AttemptAsync(first), AttemptAsync(second));
        Assert.Single(outcomes, static x => x is null);
        var conflict = Assert.IsType<DocumentConcurrencyException>(Assert.Single(outcomes, static x => x is not null));
        Assert.Equal(revision, conflict.ExpectedRevision);
        Assert.True(conflict.ActualRevision > revision);
        Assert.Equal("race", conflict.Id);
        var current = (await store.LoadAsync("tenant", Collection, "race"))!;
        Assert.True(current.Value.Name is "one" or "two");
    });

    [Fact]
    public Task Concurrent_inserts_have_one_winner_without_unique_constraint_errors() => WithStoreAsync(async (store, unusedSource) =>
    {
        using var first = store.OpenSession("tenant");
        using var second = store.OpenSession("tenant");
        first.Insert(Collection, "race", new TestDocument("one", 1, null));
        second.Insert(Collection, "race", new TestDocument("two", 2, null));
        var outcomes = await Task.WhenAll(AttemptAsync(first), AttemptAsync(second));
        Assert.Single(outcomes, static x => x is null);
        var conflict = Assert.IsType<DocumentConcurrencyException>(Assert.Single(outcomes, static x => x is not null));
        Assert.Null(conflict.ExpectedRevision);
        Assert.True(conflict.ActualRevision > 0);
    });

    [Fact]
    public Task Conflict_rolls_back_writes_across_batches_and_retains_pending_operations() => WithStoreAsync(async (store, unusedSource) =>
    {
        using var setup = store.OpenSession("tenant");
        setup.Insert(Collection, "z-existing", new TestDocument("existing", 7, null));
        _ = await setup.SaveChangesAsync();
        using var conflicted = store.OpenSession("tenant");
        for (var i = 0; i < 260; i++)
        {
            conflicted.Insert(Collection, $"a-{i:D4}", new TestDocument("must roll back", i, null));
        }

        conflicted.Insert(Collection, "z-existing", new TestDocument("duplicate", 0, null));
        _ = await Assert.ThrowsAsync<DocumentConcurrencyException>(() => conflicted.SaveChangesAsync().AsTask());
        Assert.Equal(261, conflicted.PendingCount);
        Assert.Null(await store.LoadAsync("tenant", Collection, "a-0000"));
        Assert.Null(await store.LoadAsync("tenant", Collection, "a-0259"));
        Assert.Equal("existing", (await store.LoadAsync("tenant", Collection, "z-existing"))!.Value.Name);
    });

    [Fact]
    public Task Missing_replace_or_delete_conflicts_and_does_not_create_documents() => WithStoreAsync(async (store, unusedSource) =>
    {
        using var session = store.OpenSession("tenant");
        session.Replace(Collection, "missing", new TestDocument("no", 0, null), 42);
        var conflict = await Assert.ThrowsAsync<DocumentConcurrencyException>(() => session.SaveChangesAsync().AsTask());
        Assert.Null(conflict.ActualRevision);
        session.Clear();
        session.Delete(Collection, "missing", 42);
        conflict = await Assert.ThrowsAsync<DocumentConcurrencyException>(() => session.SaveChangesAsync().AsTask());
        Assert.Null(conflict.ActualRevision);
    });

    [Fact]
    public Task Tenant_and_collection_keys_are_isolated_with_keyset_pages_and_filters() => WithStoreAsync(async (store, unusedSource) =>
    {
        using var a = store.OpenSession("a");
        using var b = store.OpenSession("b");
        var otherCollection = new DocumentCollectionDefinition<TestDocument>("other", TestJsonContext.Default.TestDocument);
        for (var i = 0; i < 7; i++)
        {
            a.Insert(Collection, $"{i:D3}", new TestDocument(i % 2 == 0 ? "even" : "odd", i, null));
            b.Insert(Collection, $"{i:D3}", new TestDocument("other tenant", i, null));
        }

        a.Insert(otherCollection, "000", new TestDocument("other collection", 100, null));
        _ = await a.SaveChangesAsync();
        _ = await b.SaveChangesAsync();
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await store.ReadPageAsync("a", Collection, 3, cursor);
            ids.AddRange(page.Items.Select(static x => x.Id));
            Assert.All(page.Items, static x => Assert.DoesNotContain("other", x.Value.Name, StringComparison.Ordinal));
            cursor = page.NextAfterId;
        } while (cursor is not null);
        Assert.Equal(["000", "001", "002", "003", "004", "005", "006"], ids);
        using var filter = JsonDocument.Parse("{\"name\":\"even\"}");
        var filtered = await store.ReadPageAsync("a", Collection, 10, contains: filter.RootElement);
        Assert.Equal(4, filtered.Items.Count);
        Assert.Equal("other tenant", (await store.LoadAsync("b", Collection, "000"))!.Value.Name);
        Assert.Equal(100, (await store.LoadAsync("a", otherCollection, "000"))!.Value.Count);
    });

    [Fact]
    public Task Patches_apply_in_order_and_deletion_recreation_cannot_reuse_revisions() => WithStoreAsync(async (store, unusedSource) =>
    {
        using var session = store.OpenSession("tenant");
        session.Insert(Collection, "1", new TestDocument("initial", 1, new Customer("Ada")));
        var revision = Assert.Single(await session.SaveChangesAsync()).Revision!.Value;
        using var name = JsonDocument.Parse("\"Grace\"");
        using var count = JsonDocument.Parse("27");
        session.Patch(Collection, "1", revision,
        [
            DocumentPatch.Set(["customer", "name"], name.RootElement),
            DocumentPatch.Set(["count"], count.RootElement),
            DocumentPatch.Remove(["name"]),
        ]);
        var patched = Assert.Single(await session.SaveChangesAsync()).Revision!.Value;
        var document = (await store.LoadAsync("tenant", Collection, "1"))!;
        Assert.Equal("Grace", document.Value.Customer!.Name);
        Assert.Equal(27, document.Value.Count);
        Assert.Null(document.Value.Name);
        session.Delete(Collection, "1", patched);
        Assert.Null(Assert.Single(await session.SaveChangesAsync()).Revision);
        Assert.Null(await store.LoadAsync("tenant", Collection, "1"));
        session.Insert(Collection, "1", new TestDocument("recreated", 5, null));
        var recreated = Assert.Single(await session.SaveChangesAsync()).Revision!.Value;
        Assert.True(recreated > patched);
        session.Replace(Collection, "1", new TestDocument("stale", 0, null), revision);
        _ = await Assert.ThrowsAsync<DocumentConcurrencyException>(() => session.SaveChangesAsync().AsTask());
        Assert.Equal("recreated", (await store.LoadAsync("tenant", Collection, "1"))!.Value.Name);
    });

    [Fact]
    public Task Declarative_indexes_are_installed_idempotently_and_reject_definition_changes() => WithStoreAsync(async (store, source) =>
    {
        var gin = new DocumentIndexDefinition("body_gin", DocumentIndexKind.JsonContainment);
        var lookup = new DocumentIndexDefinition("name_lookup", DocumentIndexKind.TextPath, Collection.Name, ["name"]);
        await store.EnsureIndexAsync(gin);
        await store.EnsureIndexAsync(gin);
        await store.EnsureIndexAsync(lookup);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnsureIndexAsync(new DocumentIndexDefinition("name_lookup", DocumentIndexKind.TextPath, Collection.Name, ["count"])).AsTask());
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_indexes WHERE schemaname = @schema AND indexname IN ('body_gin', 'name_lookup')";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "schema";
        parameter.Value = store.Options.Schema;
        command.Parameters.Add(parameter);
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    });

    [Fact]
    public Task Document_schema_migrations_are_bounded_idempotent_and_reject_downgrades() => WithStoreAsync(async (store, unusedSource) =>
    {
        var oldCollection = new DocumentCollectionDefinition<TestDocument>(Collection.Name, TestJsonContext.Default.TestDocument, 1);
        using var session = store.OpenSession("tenant");
        for (var i = 0; i < 5; i++)
        {
            session.Insert(oldCollection, $"{i:D3}", new TestDocument("old", i, null));
        }

        _ = await session.SaveChangesAsync();
        var migration = new DocumentMigration<TestDocument>(1, 2, static json => new TestDocument("migrated", json.GetProperty("count").GetInt32() + 10, new Customer("Ada")));
        var first = await store.MigratePageAsync("tenant", Collection, migration, 2);
        Assert.Equal(2, first.MigratedCount);
        Assert.Equal("001", first.NextAfterId);
        var second = await store.MigratePageAsync("tenant", Collection, migration, 2, first.NextAfterId);
        Assert.Equal(2, second.MigratedCount);
        var third = await store.MigratePageAsync("tenant", Collection, migration, 2, second.NextAfterId);
        Assert.Equal(1, third.MigratedCount);
        Assert.Null(third.NextAfterId);
        Assert.Equal(0, (await store.MigratePageAsync("tenant", Collection, migration)).MigratedCount);
        var migrated = (await store.LoadAsync("tenant", Collection, "000"))!;
        Assert.Equal(2, migrated.SchemaVersion);
        Assert.Equal(10, migrated.Value.Count);
        session.Replace(oldCollection, "000", new TestDocument("downgrade", 0, null), migrated.Revision);
        var exception = await Assert.ThrowsAsync<DocumentSchemaVersionException>(() => session.SaveChangesAsync().AsTask());
        Assert.Equal(1, exception.RequestedVersion);
        Assert.Equal(2, exception.ActualVersion);
        session.Clear();
        using var patchValue = JsonDocument.Parse("1");
        session.Patch(oldCollection, "000", migrated.Revision, [DocumentPatch.Set(["count"], patchValue.RootElement)]);
        _ = await Assert.ThrowsAsync<DocumentSchemaVersionException>(() => session.SaveChangesAsync().AsTask());
        Assert.Equal(10, (await store.LoadAsync("tenant", Collection, "000"))!.Value.Count);
    });

    [Fact]
    public Task Pages_stop_at_byte_budget_and_resume_without_skipping_documents() => WithStoreAsync(async (store, unusedSource) =>
    {
        using var session = store.OpenSession("tenant");
        for (var i = 0; i < 4; i++)
        {
            session.Insert(Collection, $"{i:D3}", new TestDocument(new string('x', 120), i, null));
        }

        _ = await session.SaveChangesAsync();
        var found = new List<string>();
        string? cursor = null;
        do
        {
            var page = await store.ReadPageAsync("tenant", Collection, 10, cursor);
            Assert.Single(page.Items);
            found.Add(page.Items[0].Id);
            cursor = page.NextAfterId;
        } while (cursor is not null);
        Assert.Equal(["000", "001", "002", "003"], found);
    }, new DocumentStoreOptions { MaxDocumentBytes = 200, MaxPageBytes = 225 });

    [Fact]
    public Task Index_catalog_drift_is_detected_instead_of_reporting_missing_index_ready() => WithStoreAsync(async (store, source) =>
    {
        var definition = new DocumentIndexDefinition("body_gin", DocumentIndexKind.JsonContainment);
        await store.EnsureIndexAsync(definition);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP INDEX \"{store.Options.Schema}\".body_gin";
        _ = await command.ExecuteNonQueryAsync();
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnsureIndexAsync(definition).AsTask());
    });

    [Fact]
    public Task Canceled_save_does_not_write_and_borrowed_source_remains_usable() => WithStoreAsync(async (store, source) =>
    {
        using var session = store.OpenSession("tenant");
        session.Insert(Collection, "1", new TestDocument("pending", 1, null));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SaveChangesAsync(cancellation.Token).AsTask());
        Assert.Null(await store.LoadAsync("tenant", Collection, "1"));
        Assert.Equal(1, session.PendingCount);
        await store.DisposeAsync();
        await using var connection = await source.OpenConnectionAsync();
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    });

    [Fact]
    public Task Concurrent_bounded_workload_preserves_committed_rows_and_hot_key_increments() => WithStoreAsync(async (store, source) =>
    {
        const int writers = 8;
        const int batches = 16;
        const int rowsPerBatch = 32;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(async writer =>
        {
            for (var batch = 0; batch < batches; batch++)
            {
                using var session = store.OpenSession("workload");
                for (var row = 0; row < rowsPerBatch; row++)
                {
                    session.Insert(Collection, $"writer-{writer:D2}-{batch:D3}-{row:D3}", new TestDocument(new string('x', 1024), row, null));
                }

                Assert.Equal(rowsPerBatch, (await session.SaveChangesAsync()).Count);
            }
        }));
        await using (var connection = await source.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT count(*) FROM \"{store.Options.Schema}\".documents WHERE tenant = 'workload'";
            Assert.Equal((long)writers * batches * rowsPerBatch, await command.ExecuteScalarAsync());
        }

        using (var initial = store.OpenSession("hot-key"))
        {
            initial.Insert(Collection, "counter", new TestDocument("counter", 0, null));
            _ = await initial.SaveChangesAsync();
        }

        const int increments = 16;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(async writer =>
        {
            for (var increment = 0; increment < increments; increment++)
            {
                var committed = false;
                for (var attempt = 0; attempt < 256 && !committed; attempt++)
                {
                    var current = (await store.LoadAsync("hot-key", Collection, "counter"))!;
                    using var session = store.OpenSession("hot-key");
                    session.Replace(Collection, "counter", current.Value with { Count = current.Value.Count + 1 }, current.Revision);
                    committed = await AttemptAsync(session) is null;
                }

                Assert.True(committed, $"Writer {writer} failed bounded retries.");
            }
        }));
        Assert.Equal(writers * increments, (await store.LoadAsync("hot-key", Collection, "counter"))!.Value.Count);
    });

    [Fact]
    public Task Storage_initialization_is_concurrent_idempotent_and_checks_configuration() => WithStoreAsync(async (store, source) =>
    {
        await using var second = new DocumentStore(source, store.Options);
        await Task.WhenAll(store.InitializeAsync().AsTask(), second.InitializeAsync().AsTask());
        await using var mismatch = new DocumentStore(source, store.Options with { MaxDocumentBytes = 1024 });
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => mismatch.InitializeAsync().AsTask());
    });

    private static async Task<Exception?> AttemptAsync(DocumentSession session)
    {
        try
        {
            _ = await session.SaveChangesAsync();
            return null;
        }
        catch (DocumentConcurrencyException exception)
        {
            return exception;
        }
    }

    private static async Task WithStoreAsync(Func<DocumentStore, DbDataSource, Task> test, DocumentStoreOptions? options = null)
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        await using var source = BlueTuskDataSource.Create(connectionString);
        var schema = "documents_test_" + Guid.NewGuid().ToString("N");
        await using var store = new DocumentStore(source, (options ?? new DocumentStoreOptions()) with { Schema = schema });
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
}

public sealed record TestDocument(string? Name, int Count, Customer? Customer);
public sealed record Customer(string Name);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TestDocument))]
[JsonSerializable(typeof(int))]
internal sealed partial class TestJsonContext : JsonSerializerContext;

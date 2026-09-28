using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Documents.Tests;

public sealed class DocumentContentTests
{
    private static readonly DocumentCollectionDefinition<TestDocument> Collection = new("orders", TestJsonContext.Default.TestDocument, 2);

    [Fact]
    public Task Content_is_tenant_scoped_revision_checked_and_independent_of_json_queries() => WithStoreAsync(async (store, source, schema) =>
    {
        using (var write = store.OpenSession("alpha"))
        {
            write.Insert(Collection, "one", new TestDocument("one", 1, null));
            write.Insert(Collection, "two", new TestDocument("two", 2, null));
            _ = await write.SaveChangesAsync();
        }

        using (var write = store.OpenSession("beta"))
        {
            write.Insert(Collection, "one", new TestDocument("one", 1, null));
            _ = await write.SaveChangesAsync();
        }

        var original = (await store.LoadAsync("alpha", Collection, "one"))!;
        var bytes = new byte[64 * 1024];
        RandomNumberGenerator.Fill(bytes);
        var expected = bytes.ToArray();
        var revision = await store.AttachContentAsync("alpha", Collection, "one", original.Revision, bytes);
        bytes[0] ^= 0xff;
        var secondRevision = await store.AttachContentAsync("alpha", Collection, "two", (await store.LoadAsync("alpha", Collection, "two"))!.Revision, expected);
        _ = await store.AttachContentAsync("beta", Collection, "one", (await store.LoadAsync("beta", Collection, "one"))!.Revision, expected);
        Assert.NotEqual(original.Revision, revision);
        Assert.NotEqual(revision, secondRevision);
        Assert.Equal(2, await CountAsync(source, $"SELECT count(*) FROM \"{schema}\".content"));
        Assert.Equal(3, await CountAsync(source, $"SELECT count(*) FROM \"{schema}\".content_links"));

        var content = (await store.LoadContentAsync("alpha", Collection, "one"))!;
        Assert.Equal(expected, content.Bytes.ToArray());
        Assert.Equal(revision, content.Revision);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), content.Sha256);
        Assert.Equal(expected, (await store.LoadContentAsync("beta", Collection, "one"))!.Bytes.ToArray());
        using var filter = JsonDocument.Parse("{\"count\":1}");
        Assert.Single((await store.ReadPageAsync("alpha", Collection, contains: filter.RootElement)).Items);
        Assert.Equal("one", (await store.LoadAsync("alpha", Collection, "one"))!.Value.Name);

        using (var stale = store.OpenSession("alpha"))
        {
            stale.Replace(Collection, "one", new TestDocument("stale", 3, null), original.Revision);
            _ = await Assert.ThrowsAsync<DocumentConcurrencyException>(() => stale.SaveChangesAsync().AsTask());
        }

        _ = await Assert.ThrowsAsync<DocumentConcurrencyException>(() => store.AttachContentAsync("alpha", Collection, "one", original.Revision, new byte[] { 1, 2, 3 }).AsTask());
        Assert.Equal(2, await CountAsync(source, $"SELECT count(*) FROM \"{schema}\".content"));
        var afterRemove = await store.RemoveContentAsync("alpha", Collection, "one", revision);
        Assert.Null(await store.LoadContentAsync("alpha", Collection, "one"));
        Assert.Equal(afterRemove, (await store.LoadAsync("alpha", Collection, "one"))!.Revision);
        Assert.Equal(0, (await store.CollectUnusedContentPageAsync()).DeletedCount);
        _ = await store.RemoveContentAsync("alpha", Collection, "two", secondRevision);
        Assert.Equal(1, (await store.CollectUnusedContentPageAsync(1)).DeletedCount);
        Assert.Equal(1, await CountAsync(source, $"SELECT count(*) FROM \"{schema}\".content"));

        using (var delete = store.OpenSession("beta"))
        {
            delete.Delete(Collection, "one", (await store.LoadAsync("beta", Collection, "one"))!.Revision);
            _ = await delete.SaveChangesAsync();
        }

        Assert.Equal(1, (await store.CollectUnusedContentPageAsync(1)).DeletedCount);
        using (var reinsert = store.OpenSession("beta"))
        {
            reinsert.Insert(Collection, "one", new TestDocument("again", 4, null));
            _ = await reinsert.SaveChangesAsync();
        }

        Assert.Null(await store.LoadContentAsync("beta", Collection, "one"));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CollectUnusedContentPageAsync(1001).AsTask());
    });

    [Fact]
    public Task Digest_collision_rejects_different_bytes_and_rolls_back_revision() => WithStoreAsync(async (store, source, schema) =>
    {
        using (var write = store.OpenSession("tenant"))
        {
            write.Insert(Collection, "one", new TestDocument("one", 1, null));
            _ = await write.SaveChangesAsync();
        }

        var revision = (await store.LoadAsync("tenant", Collection, "one"))!.Revision;
        var requested = new byte[] { 1, 2, 3 };
        await using (var command = source.CreateCommand($"INSERT INTO \"{schema}\".content (tenant, digest, data) VALUES (@tenant, @digest, @data)"))
        {
            Add(command, "tenant", "tenant", DbType.String);
            Add(command, "digest", SHA256.HashData(requested), DbType.Binary);
            Add(command, "data", new byte[] { 9, 9, 9 }, DbType.Binary);
            _ = await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AttachContentAsync("tenant", Collection, "one", revision, requested).AsTask());
        Assert.Contains("digest", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(revision, (await store.LoadAsync("tenant", Collection, "one"))!.Revision);
        Assert.Null(await store.LoadContentAsync("tenant", Collection, "one"));
        await using (var link = source.CreateCommand($"INSERT INTO \"{schema}\".content_links (tenant, collection, id, digest) VALUES (@tenant, @collection, @id, @digest)"))
        {
            Add(link, "tenant", "tenant", DbType.String);
            Add(link, "collection", Collection.Name, DbType.String);
            Add(link, "id", "one", DbType.String);
            Add(link, "digest", SHA256.HashData(requested), DbType.Binary);
            _ = await link.ExecuteNonQueryAsync();
        }

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadContentAsync("tenant", Collection, "one").AsTask());
        await using (var unlink = source.CreateCommand($"DELETE FROM \"{schema}\".content_links"))
        {
            _ = await unlink.ExecuteNonQueryAsync();
        }

        Assert.Equal(1, (await store.CollectUnusedContentPageAsync(1)).DeletedCount);
    });

    [Fact]
    public Task Attachment_recovers_when_collection_deletes_an_unlinked_digest_first() => WithStoreAsync(async (store, source, schema) =>
    {
        using (var write = store.OpenSession("tenant"))
        {
            write.Insert(Collection, "one", new TestDocument("one", 1, null));
            _ = await write.SaveChangesAsync();
        }

        var bytes = new byte[] { 4, 5, 6, 7 };
        var original = (await store.LoadAsync("tenant", Collection, "one"))!.Revision;
        var linked = await store.AttachContentAsync("tenant", Collection, "one", original, bytes);
        var unlinked = await store.RemoveContentAsync("tenant", Collection, "one", linked);
        var digest = SHA256.HashData(bytes);

        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var hold = connection.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText = $"SELECT digest FROM \"{schema}\".content WHERE tenant = @tenant AND digest = @digest FOR UPDATE";
            Add(hold, "tenant", "tenant", DbType.String);
            Add(hold, "digest", digest, DbType.Binary);
            Assert.NotNull(await hold.ExecuteScalarAsync());
        }

        var attaching = store.AttachContentAsync("tenant", Collection, "one", unlinked, bytes).AsTask();
        await Task.Delay(100);
        Assert.False(attaching.IsCompleted);
        await using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = $"DELETE FROM \"{schema}\".content WHERE tenant = @tenant AND digest = @digest";
            Add(remove, "tenant", "tenant", DbType.String);
            Add(remove, "digest", digest, DbType.Binary);
            Assert.Equal(1, await remove.ExecuteNonQueryAsync());
        }

        await transaction.CommitAsync();
        var revision = await attaching.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(bytes, (await store.LoadContentAsync("tenant", Collection, "one"))!.Bytes.ToArray());
        Assert.Equal(revision, (await store.LoadAsync("tenant", Collection, "one"))!.Revision);
        Assert.Equal(1, await CountAsync(source, $"SELECT count(*) FROM \"{schema}\".content"));
    });

    [Fact]
    public Task Reattaching_64KiB_content_advances_only_document_metadata() => WithStoreAsync(async (store, source, schema) =>
    {
        using (var write = store.OpenSession("tenant"))
        {
            write.Insert(Collection, "one", new TestDocument("one", 1, null));
            _ = await write.SaveChangesAsync();
        }

        var bytes = new byte[64 * 1024];
        RandomNumberGenerator.Fill(bytes);
        var revision = await store.AttachContentAsync("tenant", Collection, "one", (await store.LoadAsync("tenant", Collection, "one"))!.Revision, bytes);
        var contentRow = await TextAsync(source, $"SELECT xmin::text FROM \"{schema}\".content");
        var linkRow = await TextAsync(source, $"SELECT xmin::text FROM \"{schema}\".content_links");
        var body = await TextAsync(source, $"SELECT body::text FROM \"{schema}\".documents");
        for (var i = 0; i < 64; i++)
        {
            revision = await store.AttachContentAsync("tenant", Collection, "one", revision, bytes);
        }

        Assert.Equal(1, await CountAsync(source, $"SELECT count(*) FROM \"{schema}\".content"));
        Assert.Equal(contentRow, await TextAsync(source, $"SELECT xmin::text FROM \"{schema}\".content"));
        Assert.Equal(linkRow, await TextAsync(source, $"SELECT xmin::text FROM \"{schema}\".content_links"));
        Assert.Equal(body, await TextAsync(source, $"SELECT body::text FROM \"{schema}\".documents"));
        Assert.Equal(revision, (await store.LoadContentAsync("tenant", Collection, "one"))!.Revision);
    });

    [Fact]
    public Task Initialization_rejects_a_missing_content_foreign_key() => WithStoreAsync(async (store, source, schema) =>
    {
        await using var command = source.CreateCommand($"ALTER TABLE \"{schema}\".content_links DROP CONSTRAINT content_links_content_fk");
        _ = await command.ExecuteNonQueryAsync();
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync().AsTask());
    });

    [Fact]
    public Task Collection_cursor_crosses_linked_keys_without_unbounded_scans() => WithStoreAsync(async (store, source, schema) =>
    {
        var payloads = Enumerable.Range(0, 8)
            .Select(static value => new[] { checked((byte)value) })
            .OrderBy(static bytes => Convert.ToHexString(SHA256.HashData(bytes)), StringComparer.Ordinal)
            .ToArray();
        using (var write = store.OpenSession("tenant"))
        {
            for (var i = 0; i < payloads.Length; i++)
            {
                write.Insert(Collection, i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), new TestDocument("linked", i, null));
            }

            _ = await write.SaveChangesAsync();
        }

        long orphanRevision = 0;
        for (var i = 0; i < payloads.Length; i++)
        {
            var id = i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            var revision = await store.AttachContentAsync("tenant", Collection, id, (await store.LoadAsync("tenant", Collection, id))!.Revision, payloads[i]);
            if (i == payloads.Length - 1)
            {
                orphanRevision = revision;
            }
        }

        _ = await store.RemoveContentAsync("tenant", Collection, "07", orphanRevision);
        string? cursor = null;
        var examined = 0;
        var deleted = 0;
        var pages = 0;
        do
        {
            var page = await store.CollectUnusedContentPageAsync(2, cursor);
            Assert.InRange(page.ExaminedCount, 0, 2);
            examined += page.ExaminedCount;
            deleted += page.DeletedCount;
            cursor = page.NextAfterCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Null(cursor);
        Assert.Equal(8, examined);
        Assert.Equal(1, deleted);
        Assert.Equal(7, await CountAsync(source, $"SELECT count(*) FROM \"{schema}\".content"));
        _ = await Assert.ThrowsAsync<ArgumentException>(() => store.CollectUnusedContentPageAsync(afterCursor: "bad").AsTask());
    });

    [Fact]
    public Task Locked_key_is_skipped_then_revisited_on_the_next_sweep() => WithStoreAsync(async (store, source, schema) =>
    {
        var payloads = new[] { new byte[] { 0 }, new byte[] { 1 } }
            .OrderBy(static bytes => Convert.ToHexString(SHA256.HashData(bytes)), StringComparer.Ordinal)
            .ToArray();
        using (var write = store.OpenSession("tenant"))
        {
            write.Insert(Collection, "one", new TestDocument("one", 1, null));
            write.Insert(Collection, "two", new TestDocument("two", 2, null));
            _ = await write.SaveChangesAsync();
        }

        var first = await store.AttachContentAsync("tenant", Collection, "one", (await store.LoadAsync("tenant", Collection, "one"))!.Revision, payloads[0]);
        _ = await store.AttachContentAsync("tenant", Collection, "two", (await store.LoadAsync("tenant", Collection, "two"))!.Revision, payloads[1]);
        _ = await store.RemoveContentAsync("tenant", Collection, "one", first);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var hold = connection.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText = $"SELECT digest FROM \"{schema}\".content WHERE tenant = @tenant AND digest = @digest FOR UPDATE";
            Add(hold, "tenant", "tenant", DbType.String);
            Add(hold, "digest", SHA256.HashData(payloads[0]), DbType.Binary);
            Assert.NotNull(await hold.ExecuteScalarAsync());
        }

        var skipped = await store.CollectUnusedContentPageAsync(1);
        Assert.Equal(1, skipped.ExaminedCount);
        Assert.Equal(0, skipped.DeletedCount);
        Assert.NotNull(skipped.NextAfterCursor);
        await transaction.CommitAsync();
        var next = await store.CollectUnusedContentPageAsync(1, skipped.NextAfterCursor);
        Assert.Equal(1, next.ExaminedCount);
        Assert.Equal(0, next.DeletedCount);
        Assert.Equal(1, (await store.CollectUnusedContentPageAsync(1)).DeletedCount);
    });

    [Fact]
    public Task Collection_cursor_supports_maximum_utf8_tenant_and_c_collation_order() => WithStoreAsync(async (store, _, _) =>
    {
        var unicodeTenant = string.Concat(Enumerable.Repeat("😀", 64)); // 256 UTF-8 bytes.
        Assert.Equal(256, System.Text.Encoding.UTF8.GetByteCount(unicodeTenant));
        foreach (var tenant in new[] { "A", unicodeTenant })
        {
            using var write = store.OpenSession(tenant);
            write.Insert(Collection, "one", new TestDocument("one", 1, null));
            if (tenant == unicodeTenant)
            {
                write.Insert(Collection, "two", new TestDocument("two", 2, null));
            }

            _ = await write.SaveChangesAsync();
        }

        _ = await store.AttachContentAsync("A", Collection, "one", (await store.LoadAsync("A", Collection, "one"))!.Revision, new byte[] { 1 });
        _ = await store.AttachContentAsync(unicodeTenant, Collection, "one", (await store.LoadAsync(unicodeTenant, Collection, "one"))!.Revision, new byte[] { 2 });
        _ = await store.AttachContentAsync(unicodeTenant, Collection, "two", (await store.LoadAsync(unicodeTenant, Collection, "two"))!.Revision, new byte[] { 3 });

        var ascii = await store.CollectUnusedContentPageAsync(1);
        Assert.Equal(1, ascii.ExaminedCount);
        var unicodeFirst = await store.CollectUnusedContentPageAsync(1, ascii.NextAfterCursor);
        Assert.Equal(1, unicodeFirst.ExaminedCount);
        Assert.Equal(388, unicodeFirst.NextAfterCursor!.Length);
        var unicodeSecond = await store.CollectUnusedContentPageAsync(1, unicodeFirst.NextAfterCursor);
        Assert.Equal(1, unicodeSecond.ExaminedCount);
        Assert.Equal(388, unicodeSecond.NextAfterCursor!.Length);
        var end = await store.CollectUnusedContentPageAsync(1, unicodeSecond.NextAfterCursor);
        Assert.Equal(0, end.ExaminedCount);
        Assert.Null(end.NextAfterCursor);
    });

    private static async Task<long> CountAsync(DbDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> TextAsync(DbDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static void Add(DbCommand command, string name, object value, DbType type)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task WithStoreAsync(Func<DocumentStore, DbDataSource, string, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        await using var source = BlueTuskDataSource.Create(connectionString);
        var schema = "documents_content_" + Guid.NewGuid().ToString("N");
        await using var store = new DocumentStore(source, new DocumentStoreOptions { Schema = schema });
        try
        {
            await store.InitializeAsync();
            await test(store, source, schema);
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            _ = await command.ExecuteNonQueryAsync();
        }
    }
}

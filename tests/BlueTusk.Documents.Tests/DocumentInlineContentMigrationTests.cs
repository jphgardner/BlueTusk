using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Documents.Tests;

public sealed class DocumentInlineContentMigrationTests
{
    private static readonly DocumentCollectionDefinition<InlineMigrationDocument> SourceCollection =
        new("orders", InlineMigrationJsonContext.Default.InlineMigrationDocument, 1);
    private static readonly DocumentCollectionDefinition<TestDocument> TargetCollection =
        new("orders", TestJsonContext.Default.TestDocument, 2);
    private static readonly DocumentInlineContentMigration<TestDocument> Migration = new(1, 2, Extract);

    [Fact]
    public Task Byte_bounded_pages_move_inline_content_atomically_and_restart_without_repeating_rows() => WithStoreAsync(
        new DocumentStoreOptions { MaxDocumentBytes = 128 * 1024, MaxPageBytes = 128 * 1024, MaxSessionBytes = 256 * 1024 },
        async (store, source, schema) =>
        {
            var payloads = new Dictionary<string, byte[]>();
            using (var write = store.OpenSession("tenant"))
            {
                for (var index = 0; index < 3; index++)
                {
                    var id = ((char)('a' + index)).ToString();
                    var bytes = new byte[48 * 1024];
                    RandomNumberGenerator.Fill(bytes);
                    payloads.Add(id, bytes);
                    write.Insert(SourceCollection, id, new InlineMigrationDocument(id, index, Convert.ToBase64String(bytes)));
                }
                _ = await write.SaveChangesAsync();
            }

            var first = await store.MigrateInlineContentPageAsync("tenant", TargetCollection, Migration, pageSize: 3);
            Assert.Equal(1, first.MigratedCount);
            Assert.Equal("a", first.NextAfterId);
            // A lost response restarts from the old cursor; committed source-version
            // rows disappear from the selection rather than being transformed twice.
            var recovered = await store.MigrateInlineContentPageAsync("tenant", TargetCollection, Migration, pageSize: 3);
            Assert.Equal(1, recovered.MigratedCount);
            Assert.Equal("b", recovered.NextAfterId);
            var last = await store.MigrateInlineContentPageAsync("tenant", TargetCollection, Migration, pageSize: 3, afterId: recovered.NextAfterId);
            Assert.Equal(1, last.MigratedCount);
            Assert.Null(last.NextAfterId);
            Assert.Equal(0, (await store.MigrateInlineContentPageAsync("tenant", TargetCollection, Migration)).MigratedCount);

            foreach (var (id, bytes) in payloads)
            {
                var typed = (await store.LoadAsync("tenant", TargetCollection, id))!;
                Assert.Equal(2, typed.SchemaVersion);
                Assert.Equal(id, typed.Value.Name);
                var content = (await store.LoadContentAsync("tenant", TargetCollection, id))!;
                Assert.Equal(typed.Revision, content.Revision);
                Assert.Equal(bytes, content.Bytes.ToArray());
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), content.Sha256);
            }
            Assert.Equal(3L, await ScalarAsync(source, $"SELECT count(*) FROM \"{schema}\".content_links"));
            Assert.Equal(0L, await ScalarAsync(source, $"SELECT count(*) FROM \"{schema}\".documents WHERE body ? 'contentBase64'"));

            using (var write = store.OpenSession("tenant"))
            {
                write.Insert(SourceCollection, "d", new InlineMigrationDocument("d", 4, "AQID"));
                _ = await write.SaveChangesAsync();
            }
            var tooLarge = new DocumentInlineContentMigration<TestDocument>(1, 2, json =>
                new(Extract(json).Value, new byte[128 * 1024]));
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.MigrateInlineContentPageAsync("tenant", TargetCollection, tooLarge).AsTask());
            Assert.Equal(1, (await store.LoadAsync("tenant", SourceCollection, "d"))!.SchemaVersion);
            Assert.Null(await store.LoadContentAsync("tenant", SourceCollection, "d"));
        });

    [Fact]
    public Task Large_first_output_fails_and_a_late_input_row_waits_for_the_next_page() => WithStoreAsync(
        new DocumentStoreOptions { MaxDocumentBytes = 128 * 1024, MaxPageBytes = 128 * 1024 },
        async (store, source, schema) =>
        {
            var bytes = new byte[56 * 1024];
            RandomNumberGenerator.Fill(bytes);
            using (var write = store.OpenSession("tenant"))
            {
                write.Insert(SourceCollection, "a", new InlineMigrationDocument("a", 1, Convert.ToBase64String(bytes)));
                write.Insert(SourceCollection, "b", new InlineMigrationDocument("b", 2, Convert.ToBase64String(bytes)));
                _ = await write.SaveChangesAsync();
            }

            var oversizedOutput = new DocumentInlineContentMigration<TestDocument>(1, 2, json =>
                new(Extract(json).Value, new byte[128 * 1024]));
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.MigrateInlineContentPageAsync("tenant", TargetCollection, oversizedOutput, pageSize: 100).AsTask());
            Assert.Equal(2L, await ScalarAsync(source, $"SELECT count(*) FROM \"{schema}\".documents WHERE schema_version=1"));
            Assert.Equal(0L, await ScalarAsync(source, $"SELECT count(*) FROM \"{schema}\".content_links"));

            var transformed = new List<string>();
            var counted = new DocumentInlineContentMigration<TestDocument>(1, 2, json =>
            {
                transformed.Add(json.GetProperty("name").GetString()!);
                return Extract(json);
            });
            var first = await store.MigrateInlineContentPageAsync("tenant", TargetCollection, counted, pageSize: 100);
            Assert.Equal(1, first.MigratedCount);
            Assert.Equal("a", first.NextAfterId);
            Assert.Collection(transformed, value => Assert.Equal("a", value));
            Assert.Equal(1, (await store.LoadAsync("tenant", SourceCollection, "b"))!.SchemaVersion);
            Assert.Null(await store.LoadContentAsync("tenant", SourceCollection, "b"));

            var second = await store.MigrateInlineContentPageAsync("tenant", TargetCollection, counted,
                pageSize: 100, afterId: first.NextAfterId);
            Assert.Equal(1, second.MigratedCount);
            Assert.Null(second.NextAfterId);
            Assert.Collection(transformed, firstValue => Assert.Equal("a", firstValue),
                secondValue => Assert.Equal("b", secondValue));
            Assert.Equal(bytes, (await store.LoadContentAsync("tenant", TargetCollection, "b"))!.Bytes.ToArray());
        });

    [Fact]
    public Task Concurrent_change_rolls_back_the_whole_page_and_existing_link_is_rejected() => WithStoreAsync(
        new DocumentStoreOptions(), async (store, source, schema) =>
        {
            using (var write = store.OpenSession("tenant"))
            {
                write.Insert(SourceCollection, "a", new InlineMigrationDocument("a", 1, "AQID"));
                write.Insert(SourceCollection, "b", new InlineMigrationDocument("b", 2, "BAUG"));
                _ = await write.SaveChangesAsync();
            }

            var previousB = (await store.LoadAsync("tenant", SourceCollection, "b"))!;
            var changed = 0;
            var racing = new DocumentInlineContentMigration<TestDocument>(1, 2, json =>
            {
                if (json.GetProperty("name").GetString() == "b" && Interlocked.Exchange(ref changed, 1) == 0)
                {
                    Task.Run(async () =>
                    {
                        using var writer = store.OpenSession("tenant");
                        writer.Replace(SourceCollection, "b", new InlineMigrationDocument("b", 9, "BAUG"), previousB.Revision);
                        _ = await writer.SaveChangesAsync();
                    }).GetAwaiter().GetResult();
                }
                return Extract(json);
            });
            _ = await Assert.ThrowsAsync<DocumentConcurrencyException>(() =>
                store.MigrateInlineContentPageAsync("tenant", TargetCollection, racing, pageSize: 2).AsTask());
            Assert.Equal(1, (await store.LoadAsync("tenant", SourceCollection, "a"))!.SchemaVersion);
            Assert.Equal(9, (await store.LoadAsync("tenant", SourceCollection, "b"))!.Value.Count);
            Assert.Equal(0L, await ScalarAsync(source, $"SELECT count(*) FROM \"{schema}\".content_links"));
            Assert.Equal(2, (await store.MigrateInlineContentPageAsync("tenant", TargetCollection, Migration, pageSize: 2)).MigratedCount);

            using (var write = store.OpenSession("tenant"))
            {
                write.Insert(SourceCollection, "c", new InlineMigrationDocument("c", 3, "BwgJ"));
                _ = await write.SaveChangesAsync();
            }
            var before = (await store.LoadAsync("tenant", SourceCollection, "c"))!;
            _ = await store.AttachContentAsync("tenant", SourceCollection, "c", before.Revision, new byte[] { 7, 8, 9 });
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.MigrateInlineContentPageAsync("tenant", TargetCollection, Migration).AsTask());
            Assert.Equal(1, (await store.LoadAsync("tenant", SourceCollection, "c"))!.SchemaVersion);
            Assert.Equal(new byte[] { 7, 8, 9 }, (await store.LoadContentAsync("tenant", SourceCollection, "c"))!.Bytes.ToArray());
        });

    private static DocumentInlineContentResult<TestDocument> Extract(JsonElement json) =>
        new(new TestDocument(json.GetProperty("name").GetString(), json.GetProperty("count").GetInt32(), null),
            Convert.FromBase64String(json.GetProperty("contentBase64").GetString()!));

    private static async Task<long> ScalarAsync(DbDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WithStoreAsync(DocumentStoreOptions options, Func<DocumentStore, DbDataSource, string, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }
        await using var source = BlueTuskDataSource.Create(connectionString);
        var schema = "documents_inline_" + Guid.NewGuid().ToString("N");
        await using var store = new DocumentStore(source, options with { Schema = schema });
        try
        {
            await store.InitializeAsync();
            await test(store, source, schema);
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            _ = await drop.ExecuteNonQueryAsync();
        }
    }
}

internal sealed record InlineMigrationDocument(string Name, int Count, string ContentBase64);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InlineMigrationDocument))]
internal sealed partial class InlineMigrationJsonContext : JsonSerializerContext;

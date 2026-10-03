using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Documents;
using static BlueTusk.UpgradeProbe.ProbeContext;

namespace BlueTusk.UpgradeProbe;

/// <summary>
/// Documents rehearsal. The baseline writes typed documents in storage version 1, attaches and then
/// removes content (an unlinked row awaiting collection) and stops part-way through an inline-content
/// migration from schema version 1 to 2. The candidate must read every document, finish the sweep
/// from the persisted cursor without repeating rows, verify every attachment, collect the orphan once,
/// reject a stale revision and a newer storage version. The baseline then reopens storage version 1,
/// reads the migrated documents and is refused a schema-version downgrade.
/// </summary>
internal static class FamilyProbe
{
    public const string Family = "Documents";
    private const string Tenant = "upgrade";
    private static readonly DocumentCollectionDefinition<OrderV1> OrdersV1 = new("orders", DocumentsProbeJson.Default.OrderV1, 1);
    private static readonly DocumentCollectionDefinition<OrderV2> OrdersV2 = new("orders", DocumentsProbeJson.Default.OrderV2, 2);
    private static readonly DocumentCollectionDefinition<NoteV1> Notes = new("notes", DocumentsProbeJson.Default.NoteV1, 1);
    private static readonly DocumentInlineContentMigration<OrderV2> ExtractNote = new(1, 2, static body =>
        new DocumentInlineContentResult<OrderV2>(
            new OrderV2(body.GetProperty("Name").GetString()!, body.GetProperty("Count").GetInt32()),
            Encoding.UTF8.GetBytes(body.GetProperty("Note").GetString()!)));

    public static async Task RunAsync(ProbeContext context)
    {
        var schema = context.SchemaBase + "_documents";
        await using var store = new DocumentStore(context.DataSource, new DocumentStoreOptions { Schema = schema });
        await context.FingerprintAsync("before", schema);
        await store.InitializeAsync();
        await context.FingerprintAsync("after", schema);
        context.Observe("StorageVersion", await context.CountAsync($"SELECT storage_version FROM \"{schema}\".storage_metadata"));

        if (context.IsSeed)
        {
            using (var session = store.OpenSession(Tenant))
            {
                for (var i = 1; i <= 4; i++)
                {
                    session.Insert(OrdersV1, Id(i), new OrderV1(Id(i), i, "note-" + Id(i)));
                }

                session.Insert(Notes, "n1", new NoteV1("kept"));
                context.Observe("DocumentsWritten", (await session.SaveChangesAsync()).Count);
            }

            var note = await store.LoadAsync(Tenant, Notes, "n1") ?? throw new InvalidOperationException("Seeded note is missing.");
            var attached = await store.AttachContentAsync(Tenant, Notes, "n1", note.Revision, Encoding.UTF8.GetBytes("orphan"));
            _ = await store.RemoveContentAsync(Tenant, Notes, "n1", attached);
            var page = await store.MigrateInlineContentPageAsync(Tenant, OrdersV2, ExtractNote, pageSize: 1);
            var cursor = page.NextAfterId ?? throw new InvalidOperationException("The baseline migration finished in one page; nothing is left in flight.");
            context.Observe("MigratedDocuments", page.MigratedCount);
            context.Observe("LinkedContent", await context.CountAsync($"SELECT count(*) FROM \"{schema}\".content_links"));
            context.Observe("OrphanedContent", await OrphanCountAsync(context, schema));
            var o2 = await store.LoadAsync(Tenant, OrdersV1, Id(2)) ?? throw new InvalidOperationException("Seeded order is missing.");
            context.WriteState(new()
            {
                ["cursor"] = cursor,
                ["o2Revision"] = o2.Revision.ToString(CultureInfo.InvariantCulture),
            });
            return;
        }

        var handoff = context.ReadState();
        if (context.IsUpgrade)
        {
            var read = await RequireOrderAsync(store, 1, 2) + await RequireNoteAsync(store);
            for (var i = 2; i <= 4; i++)
            {
                read += await RequireOrderAsync(store, i, 1);
            }

            context.Observe("DocumentsRead", read);
            var migrated = 0;
            string? cursor = handoff["cursor"];
            do
            {
                var page = await store.MigrateInlineContentPageAsync(Tenant, OrdersV2, ExtractNote, pageSize: 1, afterId: cursor);
                migrated += page.MigratedCount;
                cursor = page.NextAfterId;
            }
            while (cursor is not null);
            context.Observe("MigratedDocuments", migrated);
            context.Observe("SecondSweepMigrated", (await store.MigrateInlineContentPageAsync(Tenant, OrdersV2, ExtractNote, pageSize: 100)).MigratedCount);
            context.Observe("ContentVerified", await RequireContentAsync(store, 4));
            long collected = 0;
            string? gcCursor = null;
            do
            {
                var page = await store.CollectUnusedContentPageAsync(1000, gcCursor);
                collected += page.DeletedCount;
                gcCursor = page.NextAfterCursor;
            }
            while (gcCursor is not null);
            Require(await OrphanCountAsync(context, schema) == 0, "Unlinked content survived collection.");
            context.Observe("OrphanedContentCollected", collected);
            await RequireRejectedAsync<DocumentConcurrencyException>(async () =>
            {
                using var session = store.OpenSession(Tenant);
                session.Replace(OrdersV2, Id(2), new OrderV2("stale", 0), long.Parse(handoff["o2Revision"], CultureInfo.InvariantCulture));
                _ = await session.SaveChangesAsync();
            }, "A replacement with the baseline's stale revision overwrote a migrated document.");
            context.Observe("StaleRevisionRejected", 1);
            using (var session = store.OpenSession(Tenant))
            {
                session.Insert(OrdersV2, Id(5), new OrderV2(Id(5), 5));
                context.Observe("DocumentsWritten", (await session.SaveChangesAsync()).Count);
            }

            await RequireNewerFormatRejectedAsync(context);
            return;
        }

        var rolledBack = await RequireNoteAsync(store);
        for (var i = 1; i <= 5; i++)
        {
            rolledBack += await RequireOrderAsync(store, i, 2);
        }

        context.Observe("DocumentsRead", rolledBack);
        context.Observe("ContentVerified", await RequireContentAsync(store, 4));
        var current = await store.LoadAsync(Tenant, OrdersV2, Id(2)) ?? throw new InvalidOperationException("Migrated order is missing.");
        await RequireRejectedAsync<DocumentSchemaVersionException>(async () =>
        {
            using var session = store.OpenSession(Tenant);
            session.Replace(OrdersV1, Id(2), new OrderV1(Id(2), 2, "downgrade"), current.Revision);
            _ = await session.SaveChangesAsync();
        }, "The baseline downgraded a schema-version-2 document to version 1.");
        context.Observe("DowngradeWriteRejected", 1);
        using (var session = store.OpenSession(Tenant))
        {
            session.Insert(OrdersV1, Id(6), new OrderV1(Id(6), 6, "note-" + Id(6)));
            context.Observe("DocumentsWritten", (await session.SaveChangesAsync()).Count);
        }

        context.Observe("MigratedDocuments", (await store.MigrateInlineContentPageAsync(Tenant, OrdersV2, ExtractNote, pageSize: 100)).MigratedCount);
    }

    private static string Id(int i) => "o" + i.ToString(CultureInfo.InvariantCulture);

    private static Task<long> OrphanCountAsync(ProbeContext context, string schema) => context.CountAsync(
        $"SELECT count(*) FROM \"{schema}\".content c WHERE NOT EXISTS (SELECT 1 FROM \"{schema}\".content_links l WHERE l.tenant = c.tenant AND l.digest = c.digest)");

    private static async Task<int> RequireOrderAsync(DocumentStore store, int i, int version)
    {
        if (version == 1)
        {
            var document = await store.LoadAsync(Tenant, OrdersV1, Id(i));
            Require(document is { SchemaVersion: 1 } && document.Value == new OrderV1(Id(i), i, "note-" + Id(i)),
                $"Version-one order {Id(i)} was lost or changed across the binary boundary.");
        }
        else
        {
            var document = await store.LoadAsync(Tenant, OrdersV2, Id(i));
            Require(document is { SchemaVersion: 2 } && document.Value == new OrderV2(Id(i), i),
                $"Version-two order {Id(i)} was lost or changed across the binary boundary.");
        }

        return 1;
    }

    private static async Task<int> RequireNoteAsync(DocumentStore store)
    {
        var note = await store.LoadAsync(Tenant, Notes, "n1");
        Require(note is { SchemaVersion: 1 } && note.Value == new NoteV1("kept"), "The note document was lost or changed.");
        Require(await store.LoadContentAsync(Tenant, Notes, "n1") is null, "Removed content reappeared on the note.");
        return 1;
    }

    private static async Task<int> RequireContentAsync(DocumentStore store, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            var content = await store.LoadContentAsync(Tenant, OrdersV2, Id(i));
            Require(content is not null && Encoding.UTF8.GetString(content.Bytes.Span) == "note-" + Id(i),
                $"Extracted content for {Id(i)} was lost or changed.");
        }

        return count;
    }

    private static async Task RequireNewerFormatRejectedAsync(ProbeContext context)
    {
        // README.md: initialization never overwrites unknown storage metadata.
        var future = context.SchemaBase + "_future";
        await using (var store = new DocumentStore(context.DataSource, new DocumentStoreOptions { Schema = future }))
        {
            await store.InitializeAsync();
            await context.ExecuteAsync($"UPDATE \"{future}\".storage_metadata SET storage_version = storage_version + 1");
            await RequireRejectedAsync<InvalidOperationException>(async () => await store.InitializeAsync(),
                "The candidate initialized a Documents storage version newer than it supports.");
        }

        await context.DropSchemaAsync(future);
        context.Observe("NewerFormatRejected", 1);
    }
}

internal sealed record OrderV1(string Name, int Count, string Note);

internal sealed record OrderV2(string Name, int Count);

internal sealed record NoteV1(string Text);

[JsonSerializable(typeof(OrderV1))]
[JsonSerializable(typeof(OrderV2))]
[JsonSerializable(typeof(NoteV1))]
internal sealed partial class DocumentsProbeJson : JsonSerializerContext;

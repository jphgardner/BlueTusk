using System.Globalization;
using BlueTusk.Jobs;
using BlueTusk.Search;
using BlueTusk.Search.Jobs;
using static BlueTusk.UpgradeProbe.ProbeContext;

namespace BlueTusk.UpgradeProbe;

/// <summary>
/// Search rehearsal. The baseline indexes versioned documents and a tombstone in storage version 3,
/// leaves a full-text cursor open, queues one ingestion job and stops holding the lease of another.
/// The candidate must keep storage version 3, continue the baseline's cursor, honour the version and
/// deletion fences, process each ingestion job exactly once (the held one only after its lease
/// expires) and reject a newer storage version. The baseline then reopens storage version 3 and
/// continues a cursor the candidate opened.
/// </summary>
internal static class FamilyProbe
{
    public const string Family = "Search";
    private const string Tenant = "upgrade";
    private const string Index = "articles";
    private static readonly SearchScope Scope = new(Tenant, Index);
    private static readonly JobScope IngestionScope = new(Tenant, "search-ingestion");
    private static readonly SearchRequest Query = new() { Text = "upgrade", PageSize = 1 };

    public static async Task RunAsync(ProbeContext context)
    {
        var schema = context.SchemaBase + "_search";
        var jobSchema = context.SchemaBase + "_jobs";
        await using var search = new PostgreSqlSearchStore(context.DataSource, new SearchStoreOptions { Schema = schema });
        var jobs = new PostgreSqlJobStore(context.DataSource, new JobStoreOptions { Schema = jobSchema });
        var ingestion = new SearchIngestionJobs(jobs, search, new SearchIngestionJobOptions { IndexContract = "upgrade:full-text:v1" });
        await context.FingerprintAsync("before", schema, jobSchema);
        await search.InitializeAsync();
        await jobs.InitializeAsync();
        await context.FingerprintAsync("after", schema, jobSchema);
        context.Observe("StorageVersion", await context.CountAsync($"SELECT storage_version FROM \"{schema}\".storage_metadata"));
        var heldExpiry = $"SELECT max(lease_expires) FROM \"{jobSchema}\".jobs WHERE lease_owner = 'baseline-held'";

        if (context.IsSeed)
        {
            var applied = 0;
            foreach (var document in new[] { Document("d1", 1, "alpha"), Document("d2", 1, "beta"), Document("d2", 2, "beta revised"), Document("d3", 1, "gamma") })
            {
                applied += (await search.UpsertAsync(document)).Status == SearchIngestionStatus.Applied ? 1 : 0;
            }

            context.Observe("AppliedIngestions", applied);
            context.Observe("Tombstones", (await search.DeleteAsync(Tenant, Index, "d3", 2)).Status == SearchIngestionStatus.Applied ? 1 : 0);
            var page = await search.SearchAsync(Scope, Query);
            var cursor = page.NextCursor ?? throw new InvalidOperationException("The baseline cursor ended after one page.");
            context.Observe("CursorsOpened", 1);
            // The held job is enqueued first so the baseline's claim takes it; the second stays queued.
            _ = await ingestion.EnqueueUpsertAsync(Document("d5", 1, "epsilon"));
            var held = await jobs.ClaimAsync(IngestionScope, "baseline-held", 1, InFlightLease);
            Require(held.Count == 1, "The baseline could not claim an ingestion job.");
            _ = await ingestion.EnqueueUpsertAsync(Document("d4", 1, "delta"));
            context.Observe("QueuedIngestions", await context.CountAsync($"SELECT count(*) FROM \"{jobSchema}\".jobs WHERE status IN (0, 1)"));
            context.Observe("HeldLeases", await context.CountAsync(
                $"SELECT count(*) FROM \"{jobSchema}\".jobs WHERE lease_owner = 'baseline-held' AND lease_expires > clock_timestamp()"));
            context.WriteState(CursorState(cursor, page.Hits.Select(hit => hit.DocumentId)));
            return;
        }

        var handoff = context.ReadState();
        context.Observe("CursorContinued", await ContinueAsync(search, handoff, context.IsUpgrade ? ["d1", "d2"] : ["d1", "d2", "d4", "d5"]));
        if (context.IsUpgrade)
        {
            var fenced = 0;
            fenced += (await search.UpsertAsync(Document("d2", 1, "beta"))).Status == SearchIngestionStatus.StaleIgnored ? 1 : 0;
            fenced += (await search.UpsertAsync(Document("d2", 2, "beta revised"))).Status == SearchIngestionStatus.AlreadyApplied ? 1 : 0;
            fenced += (await search.UpsertAsync(Document("d3", 1, "gamma"))).Status == SearchIngestionStatus.StaleIgnored ? 1 : 0;
            context.Observe("FencedReplays", fenced);
            var processed = await ProcessAsync(jobs, ingestion);
            await context.RequireUnexpiredAsync(heldExpiry, "baseline ingestion lease");
            Require((await jobs.ClaimAsync(IngestionScope, "candidate-probe", 1, InFlightLease)).Count == 0,
                "The candidate claimed an ingestion job the baseline still held.");
            context.Observe("HeldLeaseHonoured", 1);
            await context.WaitForExpiryAsync(heldExpiry, "baseline ingestion lease");
            processed += await ProcessAsync(jobs, ingestion);
            context.Observe("IngestionJobsProcessed", processed);
            context.Observe("SearchableDocuments", await RequireSearchableAsync(search, ["d1", "d2", "d4", "d5"]));
            await RequireNewerFormatRejectedAsync(context);
            var page = await search.SearchAsync(Scope, Query);
            context.Observe("CursorsOpened", 1);
            context.WriteState(CursorState(page.NextCursor ?? throw new InvalidOperationException("The candidate cursor ended after one page."),
                page.Hits.Select(hit => hit.DocumentId)));
            return;
        }

        context.Observe("AppliedIngestions", (await search.UpsertAsync(Document("d6", 1, "zeta"))).Status == SearchIngestionStatus.Applied ? 1 : 0);
        context.Observe("SearchableDocuments", await RequireSearchableAsync(search, ["d1", "d2", "d4", "d5", "d6"]));
    }

    private static SearchDocument Document(string id, long version, string word) =>
        new(Tenant, Index, id, version, id, word + " upgrade rehearsal", isPublic: true);

    private static Dictionary<string, string> CursorState(SearchCursor cursor, IEnumerable<string> seen) => new()
    {
        ["query"] = cursor.QueryId.ToString("N"),
        ["after"] = cursor.AfterRank.ToString(CultureInfo.InvariantCulture),
        ["seen"] = string.Join(',', seen),
    };

    private static async Task<int> ContinueAsync(PostgreSqlSearchStore search, Dictionary<string, string> state, string[] expected)
    {
        var cursor = new SearchCursor(Guid.ParseExact(state["query"], "N"), int.Parse(state["after"], CultureInfo.InvariantCulture));
        var page = await search.ContinueSearchAsync(Scope, cursor, pageSize: 10);
        var all = state["seen"].Split(',').Concat(page.Hits.Select(hit => hit.DocumentId)).Order(StringComparer.Ordinal).ToArray();
        Require(page.NextCursor is null && all.SequenceEqual(expected),
            "A cursor opened by the other binary lost, duplicated or resurrected documents.");
        return 1;
    }

    private static async Task<int> ProcessAsync(PostgreSqlJobStore jobs, SearchIngestionJobs ingestion)
    {
        var processed = 0;
        while (true)
        {
            var leases = await jobs.ClaimAsync(IngestionScope, "candidate-ingestion", 1, InFlightLease);
            if (leases.Count == 0)
            {
                return processed;
            }

            await ingestion.ProcessLeaseAsync(leases[0]);
            Require(await jobs.CompleteAsync(leases[0]) && !await jobs.CompleteAsync(leases[0]),
                "An ingestion job completion was missing or duplicated.");
            processed++;
        }
    }

    private static async Task<int> RequireSearchableAsync(PostgreSqlSearchStore search, string[] expected)
    {
        var page = await search.SearchAsync(Scope, Query with { PageSize = 20 });
        var found = page.Hits.Select(hit => hit.DocumentId).Order(StringComparer.Ordinal).ToArray();
        Require(page.NextCursor is null && found.SequenceEqual(expected), "Searchable documents were lost, duplicated or resurrected.");
        return found.Length;
    }

    private static async Task RequireNewerFormatRejectedAsync(ProbeContext context)
    {
        // search/README.md: incompatible initialization fails instead of silently mixing formats.
        var future = context.SchemaBase + "_future";
        await using (var store = new PostgreSqlSearchStore(context.DataSource, new SearchStoreOptions { Schema = future }))
        {
            await store.InitializeAsync();
            await context.ExecuteAsync($"UPDATE \"{future}\".storage_metadata SET storage_version = storage_version + 1");
            await RequireRejectedAsync<InvalidOperationException>(async () => await store.InitializeAsync(),
                "The candidate initialized a Search storage version newer than it supports.");
        }

        await context.DropSchemaAsync(future);
        context.Observe("NewerFormatRejected", 1);
    }
}

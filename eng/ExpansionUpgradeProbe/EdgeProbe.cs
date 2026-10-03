using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using BlueTusk.Edge;
using BlueTusk.Edge.Server;
using BlueTusk.Edge.Sqlite;
using Microsoft.Data.Sqlite;
using static BlueTusk.UpgradeProbe.ProbeContext;

namespace BlueTusk.UpgradeProbe;

/// <summary>
/// Edge rehearsal over the PostgreSQL server store and the SQLite client store, synchronized in process.
/// The baseline pushes two writes, then stops after the server applied a third whose client lease it
/// still holds (a lost acknowledgement) with a fourth still queued. The candidate must keep server
/// storage version 4 and client schema 3, honour the lease until it expires, replay the third write
/// from its receipt without a second server effect, push the fourth, reject the stale lease and a newer
/// server version. The baseline then reopens both stores and pushes the write the candidate queued.
/// </summary>
internal static class FamilyProbe
{
    public const string Family = "Edge";
    private static readonly EdgeScope Scope = new("upgrade", "orders", 1);

    public static async Task RunAsync(ProbeContext context)
    {
        var schema = context.SchemaBase + "_edge";
        var databasePath = Path.Combine(context.Directory, "edge-client.db");
        await using var server = new PostgreSqlEdgeServerStore(context.DataSource, new EdgeServerOptions { Schema = schema });
        var client = new SqliteEdgeStore(new SqliteEdgeOptions { DatabasePath = databasePath });
        var coordinator = new EdgeSynchronizationCoordinator(client, new InProcessTransport(server));
        await context.FingerprintAsync("before", schema);
        await server.InitializeAsync();
        await context.FingerprintAsync("after", schema);
        await client.InitializeAsync();
        await server.ActivateScopeAsync(Scope);
        await client.ActivateScopeAsync(Scope);
        context.Observe("ServerVersion", await context.CountAsync($"SELECT version FROM \"{schema}\".metadata"));
        context.Observe("ClientVersion", await ClientScalarAsync(databasePath, "SELECT version FROM schema_metadata"));

        if (context.IsSeed)
        {
            await coordinator.SynchronizeAsync(Scope);
            await client.EnqueueAsync(Upsert("a"));
            await client.EnqueueAsync(Upsert("b"));
            await coordinator.SynchronizeAsync(Scope);
            context.Observe("PushedMutations", await context.CountAsync($"SELECT count(*) FROM \"{schema}\".receipts"));
            var lost = Upsert("c");
            await client.EnqueueAsync(lost);
            var lease = await client.ClaimAsync(Scope, InFlightLease) ?? throw new InvalidOperationException("The baseline could not lease its queued write.");
            Require(lease.Mutation.Id == lost.Id, "The baseline leased an unexpected write.");
            // The server applies the write but the baseline stops before acknowledging it locally.
            Require((await server.ApplyMutationAsync(lost)).Kind == EdgeMutationOutcomeKind.Applied, "The server did not apply the in-flight write.");
            await client.EnqueueAsync(Upsert("d"));
            context.Observe("ServerRecords", await context.CountAsync($"SELECT count(*) FROM \"{schema}\".records"));
            context.Observe("PendingMutations", await PendingAsync(client));
            context.Observe("HeldLeases", await ClientScalarAsync(databasePath, "SELECT count(*) FROM mutations WHERE status = 1 AND lease_until > " +
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
            context.WriteState(new()
            {
                ["lost"] = lost.Id.ToString("N"),
                ["fence"] = lease.Fence.ToString(CultureInfo.InvariantCulture),
                ["expires"] = lease.ExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                ["revision"] = ((await server.GetAsync(Scope, "c"))?.Revision ?? 0).ToString(CultureInfo.InvariantCulture),
            });
            return;
        }

        var handoff = context.ReadState();
        if (context.IsUpgrade)
        {
            var expires = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(handoff["expires"], CultureInfo.InvariantCulture));
            Require(DateTimeOffset.UtcNow < expires, "The baseline client lease expired before the candidate opened the store.");
            Require((await client.GetAsync(Scope, "c"))?.PendingStatus == EdgeMutationStatus.Leased,
                "The candidate did not observe the baseline's leased write.");
            context.Observe("HeldLeaseVisible", 1);
            var wait = expires - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait);
            }

            var staleFence = long.Parse(handoff["fence"], CultureInfo.InvariantCulture);
            var reclaimed = await client.ClaimAsync(Scope, InFlightLease) ?? throw new InvalidOperationException("The candidate could not reclaim the expired write.");
            Require(reclaimed.Mutation.Id == Guid.ParseExact(handoff["lost"], "N") && reclaimed.Fence > staleFence,
                "The candidate did not reclaim the baseline's write with a newer fence.");
            // The server returns the receipt it recorded for the baseline's request instead of applying again.
            var outcome = await server.ApplyMutationAsync(reclaimed.Mutation);
            var stale = new EdgeMutationLease(reclaimed.Mutation, staleFence, expires);
            await RequireRejectedAsync<EdgeLeaseLostException>(async () => await client.AcknowledgeAsync(stale, outcome),
                "The baseline's expired client lease could acknowledge over the candidate's replacement lease.");
            context.Observe("StaleLeaseRejected", 1);
            await client.AcknowledgeAsync(reclaimed, outcome);
            await coordinator.SynchronizeAsync(Scope);
            var revision = (await server.GetAsync(Scope, "c"))?.Revision ?? 0;
            Require(revision.ToString(CultureInfo.InvariantCulture) == handoff["revision"], "Replaying the lost acknowledgement applied the write twice.");
            context.Observe("RevisionsPreserved", 1);
            await client.EnqueueAsync(Upsert("e"));
            await coordinator.SynchronizeAsync(Scope);
            await client.EnqueueAsync(Upsert("f"));
            await RequireNewerFormatRejectedAsync(context);
        }
        else
        {
            await coordinator.SynchronizeAsync(Scope);
        }

        var expected = context.IsUpgrade ? 5 : 6;
        context.Observe("ServerReceipts", await context.CountAsync($"SELECT count(*) FROM \"{schema}\".receipts"));
        context.Observe("ServerRecords", await context.CountAsync($"SELECT count(*) FROM \"{schema}\".records"));
        context.Observe("CachedRecords", await RequireCacheAsync(client, server, expected));
        context.Observe("PendingMutations", await PendingAsync(client));
    }

    private static EdgeMutation Upsert(string id, Guid? mutation = null) => new(Scope, mutation ?? Guid.NewGuid(), id, 0,
        EdgeMutationKind.Upsert, Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\"}}"));

    private static async Task<long> ClientScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<int> PendingAsync(SqliteEdgeStore client) =>
        (await client.ReadPageAsync(Scope, 1000)).Items.Count(item => item.PendingMutationId is not null);

    private static async Task<int> RequireCacheAsync(SqliteEdgeStore client, PostgreSqlEdgeServerStore server, int expected)
    {
        var cached = (await client.ReadPageAsync(Scope, 1000)).Items.Where(item => item.ServerRevision > 0).ToArray();
        foreach (var item in cached)
        {
            var record = await server.GetAsync(Scope, item.Id);
            Require(record is not null && record.Revision == item.ServerRevision && record.Payload.Span.SequenceEqual(item.Payload.Span),
                $"Client cache entry {item.Id} differs from the server record.");
        }

        Require(cached.Length == expected, "Client cache lost or duplicated synchronized records.");
        return cached.Length;
    }

    private static async Task RequireNewerFormatRejectedAsync(ProbeContext context)
    {
        // edge/README.md: initialization validates the installed storage version; unknown future versions are rejected.
        var future = context.SchemaBase + "_future";
        await using (var store = new PostgreSqlEdgeServerStore(context.DataSource, new EdgeServerOptions { Schema = future }))
        {
            await store.InitializeAsync();
            await context.ExecuteAsync($"UPDATE \"{future}\".metadata SET version = version + 1");
            await RequireRejectedAsync<InvalidOperationException>(async () => await store.InitializeAsync(),
                "The candidate initialized an Edge server storage version newer than it supports.");
        }

        await context.DropSchemaAsync(future);
        context.Observe("NewerFormatRejected", 1);
    }

    private sealed class InProcessTransport(PostgreSqlEdgeServerStore server) : IEdgeRemoteTransport
    {
        public ValueTask<EdgeSnapshot> BeginSnapshotAsync(EdgeScope scope, CancellationToken cancellationToken = default) =>
            server.BeginSnapshotAsync(scope, cancellationToken);

        public async IAsyncEnumerable<IReadOnlyList<EdgeRecord>> ReadSnapshotAsync(EdgeScope scope, EdgeSnapshot snapshot,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                string? after = null;
                do
                {
                    var page = await server.ReadSnapshotPageAsync(scope, snapshot.Id, 512, after, cancellationToken);
                    if (page.Records.Count > 0)
                    {
                        yield return page.Records;
                    }

                    after = page.NextAfterId;
                }
                while (after is not null);
            }
            finally
            {
                await server.ReleaseSnapshotAsync(scope, snapshot.Id, CancellationToken.None);
            }
        }

        public ValueTask<EdgeChangeBatch?> ReadChangesAsync(EdgeScope scope, long afterPosition, int maxRecords,
            CancellationToken cancellationToken = default) => server.ReadChangesAsync(scope, afterPosition, maxRecords, cancellationToken);

        public ValueTask<EdgeMutationOutcome> ApplyMutationAsync(EdgeMutation mutation, CancellationToken cancellationToken = default) =>
            server.ApplyMutationAsync(mutation, cancellationToken);
    }
}

using System.Data.Common;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using BlueTusk.Data;
using BlueTusk.Documents;
using BlueTusk.Documents.AspNetCore;
using BlueTusk.Edge;
using BlueTusk.Edge.Http;
using BlueTusk.Edge.NativeAotSmoke;
using BlueTusk.Edge.Server;
using BlueTusk.Edge.SmokeHosting;
using BlueTusk.Edge.Sqlite;
using BlueTusk.Search;
using BlueTusk.Search.AspNetCore;
using BlueTusk.Search.PgVector;

var mainConnection = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set BLUETUSK_TEST_CONNECTION_STRING to a disposable PostgreSQL database.");
var vectorConnection = Environment.GetEnvironmentVariable("BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING to a disposable pgvector database.");
var documentSchema = "aot_documents_" + Guid.NewGuid().ToString("N");
var searchSchema = "aot_search_" + Guid.NewGuid().ToString("N");
var serverSchema = "aot_edge_server_" + Guid.NewGuid().ToString("N");
var directory = Directory.CreateTempSubdirectory("bluetusk-edge-aot-").FullName;
await using var source = BlueTuskDataSource.Create(mainConnection);
await using var vectorSource = BlueTuskDataSource.Create(vectorConnection);
try
{
    var collection = new DocumentCollectionDefinition<SmokeDocument>("orders", SmokeJsonContext.Default.SmokeDocument);
    await using (var documents = new DocumentStore(source, new DocumentStoreOptions { Schema = documentSchema }))
    {
        await documents.InitializeAsync();
        using var session = documents.OpenSession("tenant");
        session.Insert(collection, "1", new SmokeDocument("native order", 1));
        var revision = (await session.SaveChangesAsync())[0].Revision!.Value;
        session.Replace(collection, "1", new SmokeDocument("native updated", 2), revision);
        _ = await session.SaveChangesAsync();
        var loaded = await documents.LoadAsync("tenant", collection, "1");
        if (loaded?.Value.Count != 2 || await documents.LoadAsync("other", collection, "1") is not null)
        {
            throw new InvalidOperationException("Typed document or tenant boundary differs in the native executable.");
        }

        session.Insert(collection, "rollback", new SmokeDocument("must roll back", 0));
        session.Replace(collection, "1", new SmokeDocument("stale", 3), revision);
        try
        {
            _ = await session.SaveChangesAsync();
            throw new InvalidOperationException("The stale write unexpectedly succeeded.");
        }
        catch (DocumentConcurrencyException)
        {
            if (await documents.LoadAsync("tenant", collection, "rollback") is not null)
            {
                throw new InvalidOperationException("Native document conflict did not roll back the whole session.");
            }
        }
    }

    await using (var search = new PostgreSqlSearchStore(vectorSource, new SearchStoreOptions { Schema = searchSchema }, new PgVectorSearchAdapter(3), new SmokeEmbeddings()))
    {
        await search.InitializeAsync();
        _ = await search.UpsertAsync(new SearchDocument("tenant", "orders", "visible", 1, "native order", "native executable retrieval", principals: ["reader"]));
        _ = await search.UpsertAsync(new SearchDocument("tenant", "orders", "hidden", 1, "native hidden", "native executable retrieval", principals: ["owner"]));
        var scope = new SearchScope("tenant", "orders", ["reader"]);
        var results = await search.SearchAsync(scope, new SearchRequest { Mode = SearchMode.Hybrid, Text = "native" });
        if (results.Hits.Count != 1 || results.Hits[0].DocumentId != "visible")
        {
            throw new InvalidOperationException("Native hybrid retrieval differs or bypassed the ACL.");
        }

        _ = await search.DeleteAsync("tenant", "orders", "visible", 2);
        var stale = await search.UpsertAsync(new SearchDocument("tenant", "orders", "visible", 1, "stale", "native", principals: ["reader"]));
        if (stale.Status is not SearchIngestionStatus.StaleIgnored ||
            (await search.SearchAsync(scope, new SearchRequest { Text = "native" })).Hits.Count != 0)
        {
            throw new InvalidOperationException("Native tombstone version fencing differs.");
        }
    }

    var sqliteOptions = new SqliteEdgeOptions { DatabasePath = Path.Combine(directory, "edge.db") };
    var edge = new SqliteEdgeStore(sqliteOptions);
    await edge.InitializeAsync();
    var edgeScope = new EdgeScope("tenant", "orders", 1);
    await edge.ActivateScopeAsync(edgeScope);
    var snapshot = new EdgeSnapshot(Guid.NewGuid(), 4);
    await edge.BeginSnapshotAsync(edgeScope, snapshot);
    await edge.ApplySnapshotBatchAsync(edgeScope, snapshot.Id, [new EdgeRecord("1", 1, "{\"count\":1}"u8.ToArray())]);
    await edge.CommitSnapshotAsync(edgeScope, snapshot.Id);
    var mutation = new EdgeMutation(edgeScope, Guid.NewGuid(), "1", 1, EdgeMutationKind.Upsert, "{\"count\":2}"u8.ToArray());
    await edge.EnqueueAsync(mutation);
    edge = new SqliteEdgeStore(sqliteOptions);
    await edge.InitializeAsync();
    var lease = await edge.ClaimAsync(edgeScope, TimeSpan.FromMinutes(1))
        ?? throw new InvalidOperationException("The native SQLite queue did not survive reopening.");
    var outcome = new EdgeMutationOutcome(EdgeMutationOutcomeKind.Applied, new EdgeRecord("1", 2, mutation.Payload));
    await edge.AcknowledgeAsync(lease, outcome);
    await edge.AcknowledgeAsync(lease, outcome);
    var cached = await edge.GetAsync(edgeScope, "1");
    if (cached is not { ServerRevision: 2, PendingMutationId: null } || await edge.ClaimAsync(edgeScope, TimeSpan.FromMinutes(1)) is not null)
    {
        throw new InvalidOperationException("The native SQLite receipt/cache/queue acknowledgement differs.");
    }

    await using (var server = new PostgreSqlEdgeServerStore(source, new EdgeServerOptions { Schema = serverSchema }))
    {
        await server.InitializeAsync(); await server.ActivateScopeAsync(edgeScope);
        await using (var setup = source.CreateCommand($"CREATE TABLE \"{serverSchema}\".business_effects(id text PRIMARY KEY,counter integer NOT NULL)"))
        { _ = await setup.ExecuteNonQueryAsync(); }
        async ValueTask WriteBusinessAsync(DbConnection connection, DbTransaction transaction, EdgeMutation write, EdgeRecord record, CancellationToken cancellationToken)
        {
            if (record.Id != write.DocumentId) { throw new InvalidOperationException("Authoritative business record identity differs."); }
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"INSERT INTO \"{serverSchema}\".business_effects VALUES(@id,1) ON CONFLICT(id) DO UPDATE SET counter=business_effects.counter+1";
            var id = command.CreateParameter(); id.ParameterName = "id"; id.Value = write.DocumentId; command.Parameters.Add(id);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var healthDocuments = new DocumentStore(source, new DocumentStoreOptions { Schema = documentSchema });
        await using var healthSearch = new PostgreSqlSearchStore(vectorSource, new SearchStoreOptions { Schema = searchSchema }, new PgVectorSearchAdapter(3), new SmokeEmbeddings());
        await using var host = await EdgeSmokeHost.StartAsync(server, writeBusiness: WriteBusinessAsync, additionalHealthChecks: checks =>
            checks.AddBlueTuskDocuments("documents-readiness", healthDocuments, "tenant", "orders")
                .AddBlueTuskSearch("search-readiness", healthSearch, new SearchScope("tenant", "orders")));
        using var http = new HttpClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "edge-smoke-reader");
        using var remote = new HttpEdgeRemoteTransport(http, host.Endpoint);
        var firstWrite = new EdgeMutation(edgeScope, Guid.NewGuid(), "1", 0, EdgeMutationKind.Upsert, "{ \"native\" : true }"u8.ToArray());
        var first = await remote.ApplyMutationAsync(firstWrite);
        var duplicate = await remote.ApplyMutationAsync(firstWrite);
        if (first.Kind != EdgeMutationOutcomeKind.Applied || first.ServerRecord!.Revision != duplicate.ServerRecord!.Revision)
        { throw new InvalidOperationException("Native HTTP server lost the durable deduplication receipt."); }
        var remoteLocal = new SqliteEdgeStore(new SqliteEdgeOptions { DatabasePath = Path.Combine(directory, "remote.db") });
        await remoteLocal.InitializeAsync(); await remoteLocal.ActivateScopeAsync(edgeScope);
        var synchronize = new EdgeSynchronizationCoordinator(remoteLocal, remote);
        await synchronize.SynchronizeAsync(edgeScope);
        await remoteLocal.EnqueueAsync(new EdgeMutation(edgeScope, Guid.NewGuid(), "1", first.ServerRecord.Revision, EdgeMutationKind.Upsert, "{\"native\":true,\"count\":2}"u8.ToArray()));
        await synchronize.SynchronizeAsync(edgeScope);
        var synchronized = (await remoteLocal.GetAsync(edgeScope, "1"))!;
        if (synchronized.PendingMutationId is not null || (await remoteLocal.GetCheckpointAsync(edgeScope)).Position != 2 ||
            (await server.ReadChangesAsync(edgeScope, 0))?.ToPosition != 2)
        { throw new InvalidOperationException("Native server snapshot/write/feed did not commit exactly two business effects."); }
        var conflict = await remote.ApplyMutationAsync(new EdgeMutation(edgeScope, Guid.NewGuid(), "1", first.ServerRecord.Revision, EdgeMutationKind.Upsert, "{}"u8.ToArray()));
        if (conflict.Kind != EdgeMutationOutcomeKind.Conflict || conflict.ServerRecord!.Revision != synchronized.ServerRevision)
        { throw new InvalidOperationException("Native server CAS conflict differs."); }
        try { _ = await remote.BeginSnapshotAsync(new EdgeScope("other", "orders", 1)); throw new InvalidOperationException("Native server tenant authorization was bypassed."); }
        catch (EdgeHttpTransportException exception) when (exception.StatusCode == 403) { }
        using var anonymousHttp = new HttpClient(); using var anonymous = new HttpEdgeRemoteTransport(anonymousHttp, host.Endpoint);
        try { _ = await anonymous.BeginSnapshotAsync(edgeScope); throw new InvalidOperationException("Native server authentication was bypassed."); }
        catch (EdgeHttpTransportException exception) when (exception.StatusCode == 401) { }
        var health = await server.ReadHealthAsync(edgeScope);
        if (health.HeadPosition != 2 || health.RecordCount != 1 || health.ReceiptCount != 3 || health.MutationCapacityReached)
        { throw new InvalidOperationException("Native operator health counters differ from committed effects."); }
        using (var ready = await http.GetAsync(new Uri(host.Endpoint, "/ops/edge")))
        { if (!ready.IsSuccessStatusCode || await ready.Content.ReadAsStringAsync() != "Healthy") { throw new InvalidOperationException("Native authorized operator readiness failed."); } }
        using (var denied = await anonymousHttp.GetAsync(new Uri(host.Endpoint, "/ops/edge")))
        { if ((int)denied.StatusCode != 401) { throw new InvalidOperationException("Native health authorization was bypassed."); } }
        await using (var drift = source.CreateCommand($"UPDATE \"{serverSchema}\".metadata SET version=999")) { _ = await drift.ExecuteNonQueryAsync(); }
        using (var unhealthy = await http.GetAsync(new Uri(host.Endpoint, "/ops/edge")))
        { if ((int)unhealthy.StatusCode != 503 || await unhealthy.Content.ReadAsStringAsync() != "Unhealthy") { throw new InvalidOperationException("Native operator health did not reject durable format drift."); } }
        await using (var restore = source.CreateCommand($"UPDATE \"{serverSchema}\".metadata SET version=1")) { _ = await restore.ExecuteNonQueryAsync(); }
        await using (var drift = source.CreateCommand($"UPDATE \"{documentSchema}\".storage_metadata SET storage_version=999")) { _ = await drift.ExecuteNonQueryAsync(); }
        using (var unhealthy = await http.GetAsync(new Uri(host.Endpoint, "/ops/edge")))
        { if ((int)unhealthy.StatusCode != 503) { throw new InvalidOperationException("Native Documents operator probe ignored format drift."); } }
        await using (var restore = source.CreateCommand($"UPDATE \"{documentSchema}\".storage_metadata SET storage_version=1")) { _ = await restore.ExecuteNonQueryAsync(); }
        await using (var drift = vectorSource.CreateCommand($"UPDATE \"{searchSchema}\".storage_metadata SET storage_version=999")) { _ = await drift.ExecuteNonQueryAsync(); }
        using (var unhealthy = await http.GetAsync(new Uri(host.Endpoint, "/ops/edge")))
        { if ((int)unhealthy.StatusCode != 503) { throw new InvalidOperationException("Native Search operator probe ignored format drift."); } }
        await using (var restore = vectorSource.CreateCommand($"UPDATE \"{searchSchema}\".storage_metadata SET storage_version=1")) { _ = await restore.ExecuteNonQueryAsync(); }
        using (var recovered = await http.GetAsync(new Uri(host.Endpoint, "/ops/edge")))
        { if (!recovered.IsSuccessStatusCode) { throw new InvalidOperationException("Native combined operator readiness did not recover."); } }
        await using var effects = source.CreateCommand($"SELECT counter FROM \"{serverSchema}\".business_effects");
        if (await effects.ExecuteScalarAsync() is not int businessEffects || businessEffects != 2)
        { throw new InvalidOperationException("Native replay/conflict repeated external application-table effects."); }
    }

    Console.WriteLine("NativeAOT passed: Documents JSON/CAS/session rollback; Search pgvector hybrid/ACL/tombstones; Edge SQLite queue/acknowledgement and actual authenticated PostgreSQL/ASP.NET HTTP snapshot/write/dedup/conflict/feed.");
}
finally
{
    await using (var command = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{documentSchema}\" CASCADE"))
    {
        _ = await command.ExecuteNonQueryAsync();
    }

    await using (var command = vectorSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{searchSchema}\" CASCADE"))
    {
        _ = await command.ExecuteNonQueryAsync();
    }

    await using (var command = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{serverSchema}\" CASCADE"))
    {
        _ = await command.ExecuteNonQueryAsync();
    }

    SmokeCleanup.DeleteOwnedDirectory(directory);
}

namespace BlueTusk.Edge.NativeAotSmoke
{
    internal static class SmokeCleanup
    {
        internal static void DeleteOwnedDirectory(string directory)
        {
            var resolved = Path.GetFullPath(directory);
            var temporary = Path.GetFullPath(Path.GetTempPath());
            if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("bluetusk-edge-aot-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing cleanup outside the owned temporary smoke directory.");
            }

            Directory.Delete(resolved, recursive: true);
        }
    }

    internal sealed record SmokeDocument(string Name, int Count);

    [JsonSerializable(typeof(SmokeDocument))]
    internal sealed partial class SmokeJsonContext : JsonSerializerContext;

    internal sealed class SmokeEmbeddings : IScopedSearchEmbeddingProvider
    {
        public string ModelIdentity => "native-smoke-v1";
        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The native Search path must propagate authenticated embedding scope.");
        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedForScopeAsync(SearchScope scope, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            if (scope.Tenant != "tenant" || scope.Index != "orders") { throw new InvalidOperationException("Native embedding scope differs."); }
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(texts.Select(static _ => (ReadOnlyMemory<float>)new float[] { 1, 0, 0 }).ToArray());
        }
    }
}

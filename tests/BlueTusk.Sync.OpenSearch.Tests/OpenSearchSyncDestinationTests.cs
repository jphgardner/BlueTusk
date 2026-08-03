using System.Net;
using System.Text;
using System.Text.Json;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.Sync.Testing;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Sync.OpenSearch.Tests;

public sealed class OpenSearchSyncDestinationTests
{
    private static readonly ChangeSourceIdentity Source =
        new("opensearch-system", "opensearch-database", "opensearch-slot", "public:orders");

    [Fact]
    public void Options_require_an_absolute_base_address()
    {
        using var client = new HttpClient(new StubHandler());
        var options = new OpenSearchSyncOptions { Client = client };

        var exception = Assert.Throws<ArgumentException>(() => new OpenSearchSyncDestination(options));
        Assert.Contains("BaseAddress", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenSearch_recovers_partial_bulk_work_and_atomically_swaps_rebuild_aliases()
    {
        var endpoint = GetEndpoint();
        using var client = new HttpClient { BaseAddress = endpoint };
        var prefix = "bt-sync-test-" + Guid.NewGuid().ToString("N");
        var options = new OpenSearchSyncOptions
        {
            Client = client,
            IndexPrefix = prefix,
            NumberOfReplicas = 0,
            RefreshAfterWrite = true,
            MaxDocumentBytes = 1024 * 1024,
            MaxBulkBytes = 16 * 1024 * 1024,
        };
        var transform = SyncTransformVersion.Create("orders", "v1");
        var destination = new OpenSearchSyncDestination(options);
        try
        {
            var provisioned = await destination.ProvisionAsync(
                new SyncProvisionRequest("orders", Source, transform));
            Assert.Equal(SyncProvisionStatus.Ready, provisioned.Status);
            Assert.True(destination.Capabilities.HasFlag(SyncDestinationCapabilities.AliasSwap));
            Assert.False(destination.Capabilities.HasFlag(SyncDestinationCapabilities.TransactionalBatches));
            Assert.True(destination.Capabilities.HasFlag(SyncDestinationCapabilities.Reconciliation));

            await using var seedDelivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                40,
                Lsn(100));
            var seed = Batch(
                transform,
                seedDelivery.Transaction,
                [Mutation(40, 100, 0, "failures", "seed", "{\"value\":{\"nested\":true}}")]);
            Assert.Equal(
                SyncApplyStatus.Applied,
                (await destination.ApplyTransactionAsync(seed)).Status);

            await using var partialDelivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                41,
                Lsn(101));
            var partial = Batch(
                transform,
                partialDelivery.Transaction,
                [
                    Mutation(41, 101, 0, "valid", "1", "{\"status\":\"survives\"}"),
                    Mutation(41, 101, 1, "failures", "bad", "{\"value\":\"mapping-conflict\"}"),
                ]);
            await Assert.ThrowsAsync<OpenSearchSyncBulkException>(
                () => destination.ApplyTransactionAsync(partial).AsTask());
            AssertJson(
                "{\"status\":\"survives\"}",
                await destination.ReadDocumentAsync("orders", "valid", "1"));

            var recovered = Batch(
                transform,
                partialDelivery.Transaction,
                [
                    Mutation(41, 101, 0, "valid", "1", "{\"status\":\"survives\"}"),
                    Mutation(41, 101, 1, "failures", "bad", "{\"value\":{\"nested\":false}}"),
                ]);
            Assert.Equal(
                SyncApplyStatus.Applied,
                (await destination.ApplyTransactionAsync(recovered)).Status);

            await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                42,
                Lsn(105));
            var transaction = Batch(
                transform,
                delivery.Transaction,
                [Mutation(42, 105, 0, "orders", "42", "{\"status\":\"new\"}")]);
            var applied = await destination.ApplyTransactionAsync(transaction);
            Assert.Equal(SyncApplyStatus.Applied, applied.Status);
            Assert.Equal(Lsn(105), applied.DurablePosition);

            var changedDuplicate = Batch(
                transform,
                delivery.Transaction,
                [Mutation(42, 105, 0, "orders", "42", "{\"status\":\"wrong\"}")]);
            Assert.Equal(
                SyncApplyStatus.AlreadyApplied,
                (await destination.ApplyTransactionAsync(changedDuplicate)).Status);
            AssertJson(
                "{\"status\":\"new\"}",
                await destination.ReadDocumentAsync("orders", "orders", "42"));

            await using var resetDelivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                43,
                Lsn(106));
            var resetTransaction = Batch(
                transform,
                resetDelivery.Transaction,
                [
                    Mutation(43, 106, 0, "orders", "42", "{\"status\":\"discarded\"}"),
                    new SyncMutation(
                        new ChangeId(Source, Lsn(106), 43, 1),
                        SyncMutationKind.DeleteCollection,
                        "orders",
                        null,
                        ReadOnlyMemory<byte>.Empty),
                    Mutation(43, 106, 2, "orders", "42", "{\"status\":\"final\"}"),
                ]);
            _ = await destination.ApplyTransactionAsync(resetTransaction);
            AssertJson(
                "{\"status\":\"final\"}",
                await destination.ReadDocumentAsync("orders", "orders", "42"));

            var restarted = new OpenSearchSyncDestination(options);
            Assert.Equal(
                SyncProvisionStatus.Ready,
                (await restarted.ProvisionAsync(
                    new SyncProvisionRequest("orders", Source, transform))).Status);
            Assert.Equal(
                SyncApplyStatus.AlreadyApplied,
                (await restarted.ApplyTransactionAsync(resetTransaction)).Status);

            var epoch = new SnapshotEpoch(Guid.NewGuid(), Source, Lsn(300), DateTimeOffset.UtcNow);
            var snapshot = SnapshotBatch(transform, epoch, "50", "{\"snapshot\":true}");
            await restarted.ResetSnapshotAsync(
                "orders",
                new SnapshotReset(epoch, null, "initial bootstrap"));
            await restarted.StartSnapshotAsync("orders", new SnapshotStart(epoch, 1), transform);
            await restarted.ApplySnapshotBatchAsync(snapshot);
            await restarted.CompleteSnapshotAsync(
                "orders",
                new SnapshotComplete(epoch, 1, 1),
                transform);
            AssertJson(
                "{\"snapshot\":true}",
                await restarted.ReadDocumentAsync("orders", "orders", "50"));
            await Assert.ThrowsAsync<OpenSearchSyncSnapshotException>(
                () => restarted.ApplySnapshotBatchAsync(snapshot).AsTask());

            var quarantine = new SyncQuarantineRecord(
                "orders",
                transform,
                Source,
                44,
                Lsn(301),
                "test",
                "poison",
                DateTimeOffset.UtcNow);
            Assert.True(await restarted.StoreAsync(quarantine));
            Assert.True(await restarted.StoreAsync(quarantine));
            var identity = SyncQuarantineIdentity.FromRecord(quarantine);
            Assert.NotNull(await restarted.ReadAsync(identity));
            await using var replayDelivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                44,
                Lsn(301));
            var replayBatch = Batch(
                transform,
                replayDelivery.Transaction,
                [Mutation(44, 301, 0, "orders", "50", "{\"replayed\":true}")]);
            Assert.Equal(
                SyncQuarantineReplayApplyStatus.Applied,
                (await restarted.ReplayTransactionAsync(replayBatch, "replay-301")).Status);
            Assert.Equal(
                SyncQuarantineReplayApplyStatus.AlreadyApplied,
                (await restarted.ReplayTransactionAsync(replayBatch, "replay-301")).Status);
            Assert.Equal(
                SyncQuarantineResolutionStatus.Resolved,
                (await restarted.ResolveAsync(
                    identity,
                    transform.Fingerprint,
                    "replay-301",
                    DateTimeOffset.UtcNow)).Status);

            await using var laterDelivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                45,
                Lsn(302));
            _ = await restarted.ApplyTransactionAsync(Batch(
                transform,
                laterDelivery.Transaction,
                [Mutation(45, 302, 0, "orders", "50", "{}")]));
            Assert.Equal(
                SyncQuarantineReplayApplyStatus.CheckpointAdvanced,
                (await restarted.ReplayTransactionAsync(replayBatch, "replay-301")).Status);

            var replacementTransform = SyncTransformVersion.Create("orders", "v2");
            var replacement = new OpenSearchSyncDestination(options);
            var mismatch = await replacement.ProvisionAsync(
                new SyncProvisionRequest("orders", Source, replacementTransform));
            Assert.Equal(SyncProvisionStatus.RebuildRequired, mismatch.Status);
            Assert.Equal(transform.Fingerprint, mismatch.ExistingTransformFingerprint);
            var rebuildDestination = Assert.IsAssignableFrom<ISyncRebuildDestination>(replacement);
            var preparation = await rebuildDestination.PrepareRebuildAsync(
                new SyncProvisionRequest("orders", Source, replacementTransform));
            Assert.Equal(transform.Fingerprint[..16], preparation.PreviousGeneration);
            replacement = new OpenSearchSyncDestination(options);
            rebuildDestination = Assert.IsAssignableFrom<ISyncRebuildDestination>(replacement);
            _ = await rebuildDestination.PrepareRebuildAsync(
                new SyncProvisionRequest("orders", Source, replacementTransform));

            var rebuildEpoch = new SnapshotEpoch(
                Guid.NewGuid(),
                Source,
                Lsn(300),
                DateTimeOffset.UtcNow);
            var rebuildSnapshot = SnapshotBatch(
                replacementTransform,
                rebuildEpoch,
                "50",
                "{\"snapshot\":true,\"transform\":2}");
            await replacement.ResetSnapshotAsync(
                "orders",
                new SnapshotReset(rebuildEpoch, null, "transform rebuild"));
            await replacement.StartSnapshotAsync(
                "orders",
                new SnapshotStart(rebuildEpoch, 1),
                replacementTransform);
            await replacement.ApplySnapshotBatchAsync(rebuildSnapshot);
            await replacement.CompleteSnapshotAsync(
                "orders",
                new SnapshotComplete(rebuildEpoch, 1, 1),
                replacementTransform);

            var verification = await replacement.VerifyRebuildAsync("orders");
            Assert.True(verification.IsMatch);
            Assert.Equal(3, verification.Collections.Count);
            Assert.True((await rebuildDestination.VerifyRebuildReadyAsync("orders")).IsMatch);
            await rebuildDestination.ActivateRebuildAsync("orders");
            AssertJson(
                "{\"snapshot\":true,\"transform\":2}",
                await replacement.ReadDocumentAsync("orders", "orders", "50"));

            var finalRestart = new OpenSearchSyncDestination(options);
            Assert.Equal(
                SyncProvisionStatus.Ready,
                (await finalRestart.ProvisionAsync(
                    new SyncProvisionRequest("orders", Source, replacementTransform))).Status);
            await ((ISyncRebuildDestination)finalRestart).RetireRebuildGenerationAsync(
                "orders",
                transform.Fingerprint[..16]);
        }
        finally
        {
            await DeleteTestIndexesAsync(client, prefix);
        }
    }

    [Fact]
    public async Task OpenSearch_sidecar_reconciliation_repairs_without_advancing_checkpoint()
    {
        var endpoint = GetEndpoint();
        using var client = new HttpClient { BaseAddress = endpoint };
        var prefix = "bt-sync-reconcile-" + Guid.NewGuid().ToString("N");
        var options = new OpenSearchSyncOptions
        {
            Client = client,
            IndexPrefix = prefix,
            NumberOfReplicas = 0,
            RefreshAfterWrite = true,
            ReconciliationPageSize = 1,
        };
        var transform = SyncTransformVersion.Create("orders", "reconciliation-v1");
        var destination = new OpenSearchSyncDestination(options);
        try
        {
            _ = await destination.ProvisionAsync(
                new SyncProvisionRequest("orders", Source, transform));
            await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                71,
                Lsn(105));
            var transaction = Batch(
                transform,
                delivery.Transaction,
                [
                    Mutation(71, 105, 0, "orders", "same", "{\"value\":1}"),
                    Mutation(71, 105, 1, "orders", "stale", "{\"value\":1}"),
                    Mutation(71, 105, 2, "orders", "extra", "{\"value\":3}"),
                ]);
            Assert.Equal(
                SyncApplyStatus.Applied,
                (await destination.ApplyTransactionAsync(transaction)).Status);

            var source = new SyncReconciliationTestReader(
                [
                    Document("same", "{\"value\":1}"),
                    Document("stale", "{\"value\":2}"),
                    Document("missing", "{\"value\":4}"),
                ]);
            var request = new SyncReconciliationRequest
            {
                PipelineId = "orders",
                Collection = "orders",
                PartitionCount = 4,
                Repair = true,
                RepairBatchSize = 2,
            };
            var repaired = await SyncReconciler.ReconcileAsync(request, source, destination);
            Assert.Equal(1, repaired.MissingFromDestination);
            Assert.Equal(1, repaired.ExtraInDestination);
            Assert.Equal(1, repaired.ContentMismatches);
            Assert.Equal(3, repaired.RepairedDifferences);
            Assert.True(repaired.RequiresVerification);

            var verified = await SyncReconciler.ReconcileAsync(
                request with { Repair = false },
                source,
                destination);
            Assert.True(verified.IsMatch);
            Assert.Equal(3, verified.MatchedKeys);
            Assert.Null(await destination.ReadDocumentAsync("orders", "orders", "extra"));
            AssertJson(
                "{\"value\":2}",
                await destination.ReadDocumentAsync("orders", "orders", "stale"));
            AssertJson(
                "{\"value\":4}",
                await destination.ReadDocumentAsync("orders", "orders", "missing"));
            Assert.Equal(
                SyncApplyStatus.AlreadyApplied,
                (await destination.ApplyTransactionAsync(transaction)).Status);
        }
        finally
        {
            await DeleteTestIndexesAsync(client, prefix);
        }
    }

    private static SyncTransactionBatch Batch(
        SyncTransformVersion transform,
        ChangeTransaction transaction,
        IReadOnlyList<SyncMutation> mutations) =>
        new("orders", transform, transaction, mutations);

    private static SyncMutation Mutation(
        uint transactionId,
        ulong position,
        int ordinal,
        string collection,
        string key,
        string content) =>
        new(
            new ChangeId(Source, Lsn(position), transactionId, ordinal),
            SyncMutationKind.Upsert,
            collection,
            key,
            Encoding.UTF8.GetBytes(content),
            "application/json");

    private static SyncSnapshotBatch SnapshotBatch(
        SyncTransformVersion transform,
        SnapshotEpoch epoch,
        string key,
        string content)
    {
        var table = new ChangeTable(
            7,
            "public",
            "orders",
            'd',
            [new ChangeColumn(0, "id", 23, -1, true)]);
        return new SyncSnapshotBatch(
            "orders",
            transform,
            new ChangeSnapshotBatch(epoch, table, 0, [], true),
            [new SyncSnapshotMutation(
                new SnapshotRowId(epoch.Value, "public.orders", key),
                "orders",
                key,
                Encoding.UTF8.GetBytes(content),
                "application/json")]);
    }

    private static SyncReconciliationTestDocument Document(string key, string content) =>
        new(key, Encoding.UTF8.GetBytes(content));

    private static void AssertJson(string expected, ReadOnlyMemory<byte>? actual)
    {
        Assert.NotNull(actual);
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual.Value);
        Assert.True(JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement));
    }

    private static async Task DeleteTestIndexesAsync(HttpClient client, string prefix)
    {
        using var list = await client.GetAsync(
            $"_cat/indices/{prefix}-*?format=json&h=index",
            TestContext.Current.CancellationToken);
        if (list.StatusCode is HttpStatusCode.NotFound)
        {
            return;
        }

        list.EnsureSuccessStatusCode();
        var payload = await list.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(payload);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var index = item.GetProperty("index").GetString();
            if (index is null || !index.StartsWith(prefix + "-", StringComparison.Ordinal))
            {
                continue;
            }

            using var response = await client.DeleteAsync(index, TestContext.Current.CancellationToken);
            if (response.StatusCode is not HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }
        }
    }

    private static Uri GetEndpoint()
    {
        var value = Environment.GetEnvironmentVariable("BLUETUSK_OPENSEARCH_URL");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw SkipException.ForSkip("BLUETUSK_OPENSEARCH_URL is not configured.");
        }

        return new Uri(value.EndsWith('/') ? value : value + '/', UriKind.Absolute);
    }

    private static BlueTuskLogSequenceNumber Lsn(ulong value) => new(value);

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

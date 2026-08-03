using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using NATS.Client.Core;
using NATS.Net;
using Xunit.Sdk;

namespace BlueTusk.Sync.Nats.Tests;

public sealed class NatsSyncDestinationTests
{
    private static readonly ChangeSourceIdentity Source =
        new("nats-system", "nats-database", "nats-slot", "public:orders");

    [Fact]
    public async Task JetStream_persists_whole_transactions_and_deduplicates_stable_ids()
    {
        var url = GetNatsUrl();
        await using var client = new NatsClient(NatsOpts.Default with
        {
            Url = url,
            Name = "bluetusk-sync-tests",
        });
        await client.ConnectAsync();
        var jetStream = client.CreateJetStreamContext();
        var suffix = Guid.NewGuid().ToString("N");
        var streamName = "BT_SYNC_" + suffix.ToUpperInvariant();
        var options = new NatsSyncOptions
        {
            JetStream = jetStream,
            StreamName = streamName,
            SubjectPrefix = "bluetusk.sync." + suffix,
            MaxAge = TimeSpan.FromHours(1),
            MaxBytes = 16 * 1024 * 1024,
            MaxMessageBytes = 1024 * 1024,
            DuplicateWindow = TimeSpan.FromMinutes(30),
        };
        var destination = new NatsSyncDestination(options);
        var transform = SyncTransformVersion.Create("orders", "v1");
        try
        {
            var provisioned = await destination.ProvisionAsync(
                new SyncProvisionRequest("orders", Source, transform));
            Assert.Equal(SyncProvisionStatus.Ready, provisioned.Status);

            await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                42,
                new BlueTuskLogSequenceNumber(105));
            var batch = new SyncTransactionBatch(
                "orders",
                transform,
                delivery.Transaction,
                [new SyncMutation(
                    new ChangeId(Source, new BlueTuskLogSequenceNumber(105), 42, 0),
                    SyncMutationKind.Upsert,
                    "orders",
                    "42",
                    "{}"u8.ToArray(),
                    "application/json")]);

            var applied = await destination.ApplyTransactionAsync(batch);
            var duplicate = await destination.ApplyTransactionAsync(batch);
            Assert.Equal(SyncApplyStatus.Applied, applied.Status);
            Assert.Equal(new BlueTuskLogSequenceNumber(105), applied.DurablePosition);
            Assert.Equal(SyncApplyStatus.AlreadyApplied, duplicate.Status);

            var stream = await jetStream.GetStreamAsync(streamName);
            Assert.Equal(1L, stream.Info.State.Messages);

            var epoch = new SnapshotEpoch(
                Guid.NewGuid(),
                Source,
                new BlueTuskLogSequenceNumber(100),
                DateTimeOffset.UtcNow);
            var table = new ChangeTable(
                7,
                "public",
                "orders",
                'd',
                [new ChangeColumn(0, "id", 23, -1, true)]);
            var snapshotBatch = new SyncSnapshotBatch(
                "orders",
                transform,
                new ChangeSnapshotBatch(epoch, table, 0, [], true),
                [new SyncSnapshotMutation(
                    new SnapshotRowId(epoch.Value, "public.orders", "42"),
                    "orders",
                    "42",
                    "{}"u8.ToArray(),
                    "application/json")]);
            var reset = new SnapshotReset(epoch, null, "initial bootstrap");
            var start = new SnapshotStart(epoch, 1);
            var complete = new SnapshotComplete(epoch, 1, 1);
            await destination.ResetSnapshotAsync("orders", reset);
            await destination.StartSnapshotAsync("orders", start, transform);
            await destination.ApplySnapshotBatchAsync(snapshotBatch);
            await destination.CompleteSnapshotAsync("orders", complete, transform);
            await destination.ResetSnapshotAsync("orders", reset);
            await destination.StartSnapshotAsync("orders", start, transform);
            await destination.ApplySnapshotBatchAsync(snapshotBatch);
            await destination.CompleteSnapshotAsync("orders", complete, transform);

            stream = await jetStream.GetStreamAsync(streamName);
            Assert.Equal(5L, stream.Info.State.Messages);

            var restarted = new NatsSyncDestination(options);
            var restartedProvision = await restarted.ProvisionAsync(
                new SyncProvisionRequest("orders", Source, transform));
            var replay = await restarted.ApplyTransactionAsync(batch);
            Assert.Equal(SyncProvisionStatus.Ready, restartedProvision.Status);
            Assert.Equal(SyncApplyStatus.AlreadyApplied, replay.Status);
            stream = await jetStream.GetStreamAsync(streamName);
            Assert.Equal(5L, stream.Info.State.Messages);

            var changedTransform = SyncTransformVersion.Create("orders", "v2");
            var replacement = new NatsSyncDestination(options);
            var mismatch = await replacement.ProvisionAsync(
                new SyncProvisionRequest("orders", Source, changedTransform));
            Assert.Equal(SyncProvisionStatus.RebuildRequired, mismatch.Status);
            Assert.Equal(transform.Fingerprint, mismatch.ExistingTransformFingerprint);
        }
        finally
        {
            _ = await jetStream.DeleteStreamAsync(streamName);
        }
    }

    private static string GetNatsUrl()
    {
        var url = Environment.GetEnvironmentVariable("BLUETUSK_NATS_URL");
        return string.IsNullOrWhiteSpace(url)
            ? throw SkipException.ForSkip("BLUETUSK_NATS_URL is not configured.")
            : url;
    }
}

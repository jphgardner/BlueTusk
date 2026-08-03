using BlueTusk.Streams;
using BlueTusk.Sync.Testing;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using Xunit.Sdk;

namespace BlueTusk.Sync.Nats.Tests;

public sealed class NatsSyncConformanceTests
{
    [Fact]
    public async Task Nats_passes_shared_destination_conformance()
    {
        var url = Environment.GetEnvironmentVariable("BLUETUSK_NATS_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            throw SkipException.ForSkip("BLUETUSK_NATS_URL is not configured.");
        }

        await using var client = new NatsClient(NatsOpts.Default with
        {
            Url = url,
            Name = "bluetusk-sync-conformance",
        });
        await client.ConnectAsync();
        var jetStream = client.CreateJetStreamContext();
        var suffix = Guid.NewGuid().ToString("N");
        var streamName = "BT_SYNC_CONF_" + suffix.ToUpperInvariant();
        var harness = new NatsHarness(jetStream, streamName, "bluetusk.sync.conf." + suffix);
        try
        {
            var result = await SyncDestinationConformanceSuite.VerifyAsync(harness);

            Assert.False(result.QuarantineVerified);
            Assert.True(result.Capabilities.HasFlag(SyncDestinationCapabilities.IdempotentUpserts));
        }
        finally
        {
            _ = await jetStream.DeleteStreamAsync(streamName);
        }
    }

    private sealed class NatsHarness(
        INatsJSContext jetStream,
        string streamName,
        string subjectPrefix) : ISyncDestinationConformanceHarness
    {
        private readonly NatsSyncOptions _options = new()
        {
            JetStream = jetStream,
            StreamName = streamName,
            SubjectPrefix = subjectPrefix,
            MaxAge = TimeSpan.FromHours(1),
            MaxBytes = 16 * 1024 * 1024,
            MaxMessageBytes = 1024 * 1024,
            DuplicateWindow = TimeSpan.FromMinutes(30),
        };

        public string PipelineId => "conformance";

        public ChangeSourceIdentity Source { get; } =
            new("conformance-system", "conformance-database", "conformance-slot", "public:conformance");

        public ValueTask<ISyncDestination> CreateDestinationAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISyncDestination>(new NatsSyncDestination(_options));
        }

        public async ValueTask VerifyDurableStateAsync(
            SyncDestinationConformanceStage stage,
            ISyncDestination destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsType<NatsSyncDestination>(destination);
            var stream = await jetStream.GetStreamAsync(
                streamName,
                request: null,
                cancellationToken: cancellationToken);
            var expectedMessages = stage is SyncDestinationConformanceStage.SnapshotApplied or
                SyncDestinationConformanceStage.SnapshotRestart
                ? 4
                : 5;
            Assert.Equal(expectedMessages, stream.Info.State.Messages);
        }
    }
}

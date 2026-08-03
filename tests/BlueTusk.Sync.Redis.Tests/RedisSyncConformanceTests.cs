using System.Text;
using BlueTusk.Streams;
using BlueTusk.Sync.Testing;
using StackExchange.Redis;
using Xunit.Sdk;

namespace BlueTusk.Sync.Redis.Tests;

public sealed class RedisSyncConformanceTests
{
    [Fact]
    public async Task Redis_passes_shared_destination_conformance()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_REDIS_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_REDIS_CONNECTION_STRING is not configured.");
        }

        await using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = "bluetusk:sync:conformance:" + Guid.NewGuid().ToString("N");
        var harness = new RedisHarness(connection, prefix);
        try
        {
            var result = await SyncDestinationConformanceSuite.VerifyAsync(harness);

            Assert.True(result.QuarantineVerified);
            Assert.True(result.Capabilities.HasFlag(SyncDestinationCapabilities.CoLocatedCheckpoint));
        }
        finally
        {
            await DeleteKeysAsync(connection, prefix + ":*");
        }
    }

    private static async Task DeleteKeysAsync(ConnectionMultiplexer connection, string pattern)
    {
        var database = connection.GetDatabase();
        foreach (var endpoint in connection.GetEndPoints())
        {
            var server = connection.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: pattern))
            {
                _ = await database.KeyDeleteAsync(key);
            }
        }
    }

    private sealed class RedisHarness(IConnectionMultiplexer connection, string prefix)
        : ISyncDestinationConformanceHarness
    {
        private readonly RedisSyncOptions _options = new()
        {
            Connection = connection,
            KeyPrefix = prefix,
            MaxDocumentBytes = 1024 * 1024,
            MaxTransactionBytes = 4 * 1024 * 1024,
        };

        public string PipelineId => "conformance";

        public ChangeSourceIdentity Source { get; } =
            new("conformance-system", "conformance-database", "conformance-slot", "public:conformance");

        public ValueTask<ISyncDestination> CreateDestinationAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISyncDestination>(new RedisSyncDestination(_options));
        }

        public async ValueTask VerifyDurableStateAsync(
            SyncDestinationConformanceStage stage,
            ISyncDestination destination,
            CancellationToken cancellationToken = default)
        {
            var redis = Assert.IsType<RedisSyncDestination>(destination);
            var document = await redis.ReadDocumentAsync(
                PipelineId,
                "conformance",
                "42",
                cancellationToken);
            Assert.NotNull(document);
            var expected = stage is SyncDestinationConformanceStage.SnapshotApplied or
                SyncDestinationConformanceStage.SnapshotRestart
                ? "{\"stage\":\"snapshot\"}"
                : "{\"stage\":\"transaction\"}";
            Assert.Equal(expected, Encoding.UTF8.GetString(document.Content.Span));
        }
    }
}

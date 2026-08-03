using BlueTusk.Streams.Storage.Redis;
using BlueTusk.Streams.Testing;
using StackExchange.Redis;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskStreamsRedisStateStoreIntegrationTests
{
    [Fact]
    public async Task Redis_store_passes_checkpoint_and_lease_conformance()
    {
        var connectionString = GetConnectionString();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = "bluetusk:streams:test:" + Guid.NewGuid().ToString("N");
        var store = new RedisChangeStreamStateStore(
            new RedisChangeStreamStateStoreOptions
            {
                Connection = connection,
                KeyPrefix = prefix,
            });

        try
        {
            var report = await ChangeStreamStateStoreConformance.RunAsync(store, "redis");

            Assert.Equal("redis", report.StoreName);
            Assert.True(report.Assertions >= 10);
        }
        finally
        {
            var database = connection.GetDatabase();
            foreach (var endpoint in connection.GetEndPoints())
            {
                var server = connection.GetServer(endpoint);
                await foreach (var key in server.KeysAsync(pattern: prefix + ":*"))
                {
                    _ = await database.KeyDeleteAsync(key);
                }
            }
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_REDIS_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip(
                "BLUETUSK_TEST_REDIS_CONNECTION_STRING is not configured.")
            : connectionString;
    }
}

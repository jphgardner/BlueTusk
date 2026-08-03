using BlueTusk.Data;
using BlueTusk.Streams.Storage.PostgreSql;
using BlueTusk.Streams.Testing;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskStreamsStateStoreIntegrationTests
{
    [Fact]
    public async Task PostgreSql_store_passes_checkpoint_and_lease_conformance()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_streams_test_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var options = new PostgreSqlStreamsStorageOptions
        {
            ControlDataSource = dataSource,
            ControlSchema = schema,
        };
        var store = new PostgreSqlChangeStreamStateStore(options);
        try
        {
            await store.InitializeAsync();

            var report = await ChangeStreamStateStoreConformance.RunAsync(
                store,
                "postgresql");

            Assert.Equal("postgresql", report.StoreName);
            Assert.True(report.Assertions >= 10);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            _ = await command.ExecuteNonQueryAsync();
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.")
            : connectionString;
    }
}

using BlueTusk.Client;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class DataSourceIntegrationTests
{
    [Fact]
    public async Task EF_contexts_reuse_the_data_source_pool_without_owning_the_data_source()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString())
        {
            MaximumPoolSize = 1,
        };
        await using var dataSource = BlueTuskDataSource.Create(settings.ConnectionString);
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseBlueTusk(dataSource)
            .Options;
        int firstBackend;

        await using (var context = new TestContext(options))
        {
            await context.Database.OpenConnectionAsync();
            firstBackend = await GetBackendProcessIdAsync(
                (BlueTuskConnection)context.Database.GetDbConnection());
        }

        await using (var context = new TestContext(options))
        {
            await context.Database.OpenConnectionAsync();
            Assert.Equal(
                firstBackend,
                await GetBackendProcessIdAsync((BlueTuskConnection)context.Database.GetDbConnection()));
        }

        var statistics = dataSource.GetPoolStatistics();
        Assert.Equal(1, statistics.Total);
        Assert.Equal(1, statistics.Idle);
        Assert.Equal(1, statistics.Opened);
        Assert.Equal(1, statistics.Reused);

        await using var ownerConnection = await dataSource.OpenConnectionAsync();
        Assert.Equal(firstBackend, await GetBackendProcessIdAsync(ownerConnection));
    }

    [Fact]
    public async Task Existing_connection_ownership_is_honoured_by_sync_and_async_context_disposal()
    {
        var connectionString = GetConnectionString();
        await using var callerOwned = new BlueTuskConnection(connectionString);
        await callerOwned.OpenAsync();
        var callerOwnedOptions = new DbContextOptionsBuilder<TestContext>()
            .UseBlueTusk(callerOwned)
            .Options;

        await using (var context = new TestContext(callerOwnedOptions))
        {
            Assert.Equal(1, await ExecuteScalarAsync(callerOwned, "SELECT 1::int4"));
        }

        Assert.Equal(2, await ExecuteScalarAsync(callerOwned, "SELECT 2::int4"));

        var contextOwned = new BlueTuskConnection(connectionString);
        await contextOwned.OpenAsync();
        var contextOwnedOptions = new DbContextOptionsBuilder<TestContext>()
            .UseBlueTusk(contextOwned, contextOwnsConnection: true)
            .Options;

        await using (var context = new TestContext(contextOwnedOptions))
        {
            Assert.Same(contextOwned, context.Database.GetDbConnection());
            Assert.Equal(3, await ExecuteScalarAsync(contextOwned, "SELECT 3::int4"));
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => contextOwned.OpenAsync());
    }

    private static async Task<int> GetBackendProcessIdAsync(BlueTuskConnection connection) =>
        await ExecuteScalarAsync(connection, "SELECT pg_backend_pid()::int4");

    private static async Task<int> ExecuteScalarAsync(BlueTuskConnection connection, string sql)
    {
        await using var command = new BlueTuskCommand(sql, connection);
        return await command.ExecuteScalarAsync<int>();
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        }.ConnectionString;
    }

    private sealed class TestContext(DbContextOptions<TestContext> options) : DbContext(options);
}

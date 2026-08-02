using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Extensions.TimescaleDB.Tests;

public sealed class BlueTuskTimescaleDbTests
{
    [Fact]
    public void Build_carries_feature_only_registration_in_an_immutable_snapshot()
    {
        var builder = new BlueTuskDataSourceBuilder(
            "Host=localhost;Username=test;Password=test");
        Assert.Same(builder, builder.UseTimescaleDb("Application Extensions"));

        using var dataSource = builder.Build();
        builder.Features.Register("test.late-feature", 42);

        var feature = dataSource.Features.GetRequired<BlueTuskTimescaleDbFeature>(
            BlueTuskTimescaleDbFeature.RegistryName);
        Assert.Equal("Application Extensions", feature.Schema);
        Assert.False(dataSource.Features.Contains("test.late-feature"));
        Assert.DoesNotContain(
            dataSource.TypeRegistry.Types,
            type => type.Schema == "Application Extensions");
    }

    [Fact]
    public async Task Operations_require_explicit_TimescaleDB_registration()
    {
        await using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=localhost;Username=test;Password=test")
            .Build();

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await dataSource.GetTimescaleDbVersionAsync());
    }

    [Fact]
    public async Task Policy_helpers_validate_intervals_and_refresh_windows_before_connecting()
    {
        await using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=localhost;Port=1;Username=test;Password=test")
            .UseTimescaleDb()
            .Build();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await dataSource.AddRetentionPolicyAsync("public.metrics", BlueTuskInterval.PositiveInfinity));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await dataSource.AddColumnstorePolicyAsync("public.metrics", BlueTuskInterval.NegativeInfinity));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await dataSource.AddContinuousAggregatePolicyAsync(
                "public.hourly",
                BlueTuskInterval.PositiveInfinity,
                new BlueTuskInterval(0, 1, 0),
                new BlueTuskInterval(0, 0, 3_600_000_000)));

        var instant = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await dataSource.RefreshContinuousAggregateAsync("public.hourly", instant, instant));
    }

    [Fact]
    public async Task TimescaleDB_plugin_executes_hypertable_and_retention_lifecycle_live()
    {
        var connectionString = GetConnectionString();
        await RequireExtensionAvailableAsync(connectionString, "timescaledb");
        await using (var administration = BlueTuskDataSource.Create(connectionString))
        await using (var setup = administration.CreateCommand(
                         "CREATE EXTENSION IF NOT EXISTS timescaledb; " +
                         "DROP TABLE IF EXISTS \"timescale metrics\" CASCADE; " +
                         "CREATE TABLE \"timescale metrics\" (" +
                         "\"recorded at\" timestamptz NOT NULL, value float8 NOT NULL)"))
        {
            _ = await setup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        const string relation = "\"public\".\"timescale metrics\"";
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
            .UseTimescaleDb()
            .Build();
        try
        {
            var version = await dataSource.GetTimescaleDbVersionAsync();
            Assert.Matches(@"^\d+\.\d+\.\d+", version);

            var created = await dataSource.CreateHypertableAsync(relation, "recorded at");
            Assert.True(created.Created);
            Assert.True(created.HypertableId > 0);

            var existing = await dataSource.CreateHypertableAsync(relation, "recorded at");
            Assert.False(existing.Created);
            Assert.Equal(created.HypertableId, existing.HypertableId);

            var retention = new BlueTuskInterval(months: 0, days: 30, microseconds: 0);
            var jobId = await dataSource.AddRetentionPolicyAsync(relation, retention);
            Assert.True(jobId > 0);
            await dataSource.RemoveRetentionPolicyAsync(relation);

            await using var command = dataSource.CreateCommand(
                "SELECT count(*) FROM timescaledb_information.hypertables " +
                "WHERE hypertable_schema = 'public' AND hypertable_name = 'timescale metrics'");
            Assert.Equal(1L, await command.ExecuteScalarAsync<long>(CancellationToken.None));
        }
        finally
        {
            await using var cleanup = BlueTuskDataSource.Create(connectionString);
            await using var drop = cleanup.CreateCommand(
                "DROP TABLE IF EXISTS \"timescale metrics\" CASCADE");
            _ = await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task RequireExtensionAvailableAsync(
        string connectionString,
        string extensionName)
    {
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = $1)");
        command.Parameters.Add(new BlueTuskParameter<string>(extensionName));
        if (!await command.ExecuteScalarAsync<bool>(CancellationToken.None))
        {
            throw SkipException.ForSkip(
                $"PostgreSQL extension '{extensionName}' is not available on the configured server.");
        }
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
}

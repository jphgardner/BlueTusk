using System.Globalization;
using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskTopologyIntegrationTests
{
    [Theory]
    [InlineData(BlueTuskTargetSessionAttributes.Primary, 5830, false)]
    [InlineData(BlueTuskTargetSessionAttributes.ReadWrite, 5830, false)]
    [InlineData(BlueTuskTargetSessionAttributes.PreferPrimary, 5830, false)]
    [InlineData(BlueTuskTargetSessionAttributes.Standby, 5831, true)]
    [InlineData(BlueTuskTargetSessionAttributes.ReadOnly, 5831, true)]
    [InlineData(BlueTuskTargetSessionAttributes.PreferStandby, 5831, true)]
    public async Task Real_topology_skips_unavailable_and_incompatible_hosts(
        BlueTuskTargetSessionAttributes target,
        int expectedPort,
        bool expectedRecovery)
    {
        var settings = CreateTopologySettings();
        settings.Host = "localhost,localhost,localhost";
        settings.Ports = expectedRecovery ? "1,5830,5831" : "1,5831,5830";
        settings.TargetSessionAttributes = target;
        await using var connection = new BlueTuskConnection(settings.ConnectionString);

        await connection.OpenAsync(CancellationToken.None);

        Assert.Equal(new BlueTuskHostEndpoint("localhost", expectedPort), connection.ConnectedEndpoint);
        await using var recovery = new BlueTuskCommand("SELECT pg_is_in_recovery()", connection);
        Assert.Equal(
            expectedRecovery,
            await recovery.ExecuteScalarAsync<bool>(CancellationToken.None));
    }

    [Fact]
    public async Task Standby_replays_primary_changes_and_remains_read_only()
    {
        var table = $"bluetusk_topology_{Guid.NewGuid():N}";
        await using var primary = new BlueTuskConnection(CreateSingleHostConnectionString(5830));
        await using var standby = new BlueTuskConnection(CreateSingleHostConnectionString(5831));
        await primary.OpenAsync(CancellationToken.None);
        await standby.OpenAsync(CancellationToken.None);

        try
        {
            await ExecuteNonQueryAsync(
                primary,
                $"CREATE TABLE {table} (value int4 NOT NULL); INSERT INTO {table} VALUES (42)");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (true)
            {
                try
                {
                    await using var count = new BlueTuskCommand(
                        $"SELECT count(*) FROM {table} WHERE value = 42",
                        standby);
                    if (await count.ExecuteScalarAsync<long>(CancellationToken.None) == 1)
                    {
                        break;
                    }
                }
                catch (BlueTuskException) when (DateTime.UtcNow < deadline)
                {
                    // The relation creation may not have replayed yet.
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("The standby did not replay the primary change within 10 seconds.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            var error = await Assert.ThrowsAsync<BlueTuskException>(
                () => ExecuteNonQueryAsync(standby, $"INSERT INTO {table} VALUES (43)"));
            Assert.Contains("read-only", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ExecuteNonQueryAsync(primary, $"DROP TABLE IF EXISTS {table}");
        }
    }

    private static BlueTuskConnectionStringBuilder CreateTopologySettings()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TOPOLOGY_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TOPOLOGY_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
            Timeout = TimeSpan.FromSeconds(2),
        };
    }

    private static string CreateSingleHostConnectionString(int port)
    {
        var settings = CreateTopologySettings();
        settings.Host = "localhost";
        settings.Ports = port.ToString(CultureInfo.InvariantCulture);
        return settings.ConnectionString;
    }

    private static async Task ExecuteNonQueryAsync(BlueTuskConnection connection, string sql)
    {
        await using var command = new BlueTuskCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}

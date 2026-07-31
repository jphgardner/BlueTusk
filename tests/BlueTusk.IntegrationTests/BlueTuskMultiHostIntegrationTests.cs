using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskMultiHostIntegrationTests
{
    [Theory]
    [InlineData(BlueTuskTargetSessionAttributes.Any)]
    [InlineData(BlueTuskTargetSessionAttributes.Primary)]
    [InlineData(BlueTuskTargetSessionAttributes.ReadWrite)]
    [InlineData(BlueTuskTargetSessionAttributes.PreferPrimary)]
    [InlineData(BlueTuskTargetSessionAttributes.PreferStandby)]
    public async Task Multi_host_open_fails_over_and_selects_an_acceptable_server(
        BlueTuskTargetSessionAttributes target)
    {
        var settings = CreateSettings();
        settings.Host = "localhost,localhost";
        settings.Ports = "1,5418";
        settings.Pooling = false;
        settings.TargetSessionAttributes = target;
        await using var connection = new BlueTuskConnection(settings.ConnectionString);

        await connection.OpenAsync(CancellationToken.None);

        Assert.Equal(new BlueTuskHostEndpoint("localhost", 5418), connection.ConnectedEndpoint);
        await using var command = new BlueTuskCommand("SELECT 42::int4", connection);
        Assert.Equal(42, await command.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Theory]
    [InlineData(BlueTuskTargetSessionAttributes.Standby)]
    [InlineData(BlueTuskTargetSessionAttributes.ReadOnly)]
    public async Task Strict_target_session_selection_rejects_an_incompatible_server(
        BlueTuskTargetSessionAttributes target)
    {
        var settings = CreateSettings();
        settings.Host = "localhost";
        settings.Port = 5418;
        settings.Pooling = false;
        settings.TargetSessionAttributes = target;
        await using var connection = new BlueTuskConnection(settings.ConnectionString);

        var exception = await Assert.ThrowsAsync<BlueTuskException>(
            () => connection.OpenAsync(CancellationToken.None));

        Assert.Contains(target.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Multi_host_failure_reports_endpoints_without_credentials()
    {
        var settings = CreateSettings();
        settings.Host = "localhost,localhost";
        settings.Ports = "1,2";
        settings.Pooling = false;
        await using var connection = new BlueTuskConnection(settings.ConnectionString);

        var exception = await Assert.ThrowsAsync<BlueTuskException>(
            () => connection.OpenAsync(CancellationToken.None));

        Assert.Contains("2 configured host(s)", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(settings.Password, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multi_host_data_source_partitions_capacity_and_statistics_per_endpoint()
    {
        var settings = CreateSettings();
        settings.Host = "localhost,localhost";
        settings.Ports = "5415,5418";
        settings.Pooling = true;
        settings.MinimumPoolSize = 1;
        settings.MaximumPoolSize = 1;
        settings.TargetSessionAttributes = BlueTuskTargetSessionAttributes.Primary;
        await using var dataSource = BlueTuskDataSource.Create(settings.ConnectionString);

        await dataSource.WarmUpAsync(CancellationToken.None);
        var warmed = dataSource.GetHostPoolStatistics();
        Assert.Equal(2, warmed.Count);
        Assert.All(warmed.Values, statistics =>
        {
            Assert.Equal(1, statistics.Total);
            Assert.Equal(1, statistics.Idle);
            Assert.Equal(1, statistics.MaximumSize);
        });
        Assert.Equal(2, dataSource.GetPoolStatistics().MaximumSize);

        await using (var first = await dataSource.OpenConnectionAsync(CancellationToken.None))
        await using (var second = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            Assert.Equal(
                [5415, 5418],
                new[]
                {
                    first.ConnectedEndpoint!.Value.Port,
                    second.ConnectedEndpoint!.Value.Port,
                }.Order());
            Assert.All(
                dataSource.GetHostPoolStatistics().Values,
                statistics => Assert.Equal(1, statistics.Busy));
        }

        Assert.All(
            dataSource.GetHostPoolStatistics().Values,
            statistics => Assert.Equal(1, statistics.Idle));
        await dataSource.ClearPoolAsync();
        Assert.Equal(0, dataSource.GetPoolStatistics().Total);
    }

    private static BlueTuskConnectionStringBuilder CreateSettings()
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
            Timeout = TimeSpan.FromSeconds(2),
        };
    }
}

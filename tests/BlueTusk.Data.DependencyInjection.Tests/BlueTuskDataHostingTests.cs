using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit.Sdk;

namespace BlueTusk.Data.DependencyInjection.Tests;

public sealed class BlueTuskDataHostingTests
{
    [Fact]
    public void AddDataSource_registers_one_shared_data_source_and_health_check()
    {
        var services = new ServiceCollection();

        services.AddDataSource(
            "Host=localhost;Database=app;Username=app;Password=secret");

        using var provider = services.BuildServiceProvider();
        var concrete = provider.GetRequiredService<BlueTuskDataSource>();
        var abstraction = provider.GetRequiredService<DbDataSource>();
        var registration = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations
            .Single(item => item.Name == "bluetusk");

        Assert.Same(concrete, abstraction);
        Assert.DoesNotContain("secret", concrete.ConnectionString, StringComparison.Ordinal);
        Assert.Equal(HealthStatus.Unhealthy, registration.FailureStatus);
        Assert.Contains("ready", registration.Tags);
    }

    [Fact]
    public void AddDataSource_applies_builder_configuration_and_custom_health_name()
    {
        var services = new ServiceCollection();
        services.AddDataSource(
            "Host=localhost;Database=app",
            builder => builder.EnableMultiplexing(),
            healthCheckName: "database");

        using var provider = services.BuildServiceProvider();
        var dataSource = provider.GetRequiredService<BlueTuskDataSource>();
        var registration = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations
            .Single(item => item.Name == "database");

        Assert.True(dataSource.IsMultiplexingEnabled);
        Assert.Contains("bluetusk", registration.Tags);
    }

    [Fact]
    public async Task Health_check_completes_a_live_round_trip()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var services = new ServiceCollection();
        services.AddDataSource(connectionString);
        await using var provider = services.BuildServiceProvider();

        var result = await provider
            .GetRequiredService<BlueTuskDataSourceHealthCheck>()
            .CheckHealthAsync(
                new HealthCheckContext(),
                TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}

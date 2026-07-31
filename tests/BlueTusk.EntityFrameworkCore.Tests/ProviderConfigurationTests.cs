using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class ProviderConfigurationTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void UseBlueTusk_configures_provider_and_connection_string()
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseBlueTusk(ConnectionString)
            .Options;

        using var context = new TestContext(options);

        Assert.Equal(BlueTuskEntityFrameworkCoreInfo.ProviderName, context.Database.ProviderName);
        Assert.Equal(ConnectionString, context.Database.GetConnectionString());
        Assert.IsType<BlueTuskConnection>(context.Database.GetDbConnection());
    }

    [Fact]
    public void UseBlueTusk_accepts_an_existing_connection()
    {
        using var connection = new BlueTuskConnection(ConnectionString);
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseBlueTusk(connection)
            .Options;

        using var context = new TestContext(options);

        Assert.Same(connection, context.Database.GetDbConnection());
    }

    [Fact]
    public void AddEntityFrameworkBlueTusk_registers_provider_services()
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkBlueTusk();

        using var provider = services.BuildServiceProvider();

        Assert.Contains(
            provider.GetServices<IDatabaseProvider>(),
            candidate => candidate.Name == BlueTuskEntityFrameworkCoreInfo.ProviderName);
    }

    private sealed class TestContext(DbContextOptions<TestContext> options) : DbContext(options);
}

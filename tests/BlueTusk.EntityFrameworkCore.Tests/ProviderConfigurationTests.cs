using System.Data.Common;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public void UseBlueTusk_configures_relational_warning_defaults_without_overriding_the_caller()
    {
        var defaults = new DbContextOptionsBuilder<TestContext>()
            .UseBlueTusk(ConnectionString)
            .Options
            .FindExtension<CoreOptionsExtension>()!;
        var customized = new DbContextOptionsBuilder<TestContext>()
            .ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.AmbientTransactionWarning))
            .UseBlueTusk(ConnectionString)
            .Options
            .FindExtension<CoreOptionsExtension>()!;

        Assert.Equal(
            WarningBehavior.Throw,
            defaults.WarningsConfiguration.GetBehavior(
                RelationalEventId.AmbientTransactionWarning));
        Assert.Equal(
            WarningBehavior.Ignore,
            customized.WarningsConfiguration.GetBehavior(
                RelationalEventId.AmbientTransactionWarning));
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
    public void UseBlueTusk_accepts_an_existing_data_source()
    {
        using var dataSource = BlueTuskDataSource.Create(ConnectionString);
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseBlueTusk(dataSource)
            .Options;

        using var context = new TestContext(options);

        var exposedConnectionString = context.Database.GetConnectionString();
        Assert.Equal(dataSource.ConnectionString, exposedConnectionString);
        Assert.Null(new BlueTuskConnectionStringBuilder(exposedConnectionString!).Password);
        Assert.IsType<BlueTuskConnection>(context.Database.GetDbConnection());
    }

    [Fact]
    public void UseBlueTusk_overloads_replace_the_previous_connection_source()
    {
        using var dataSource = BlueTuskDataSource.Create(ConnectionString);
        using var connection = new BlueTuskConnection(ConnectionString);
        var builder = new DbContextOptionsBuilder<TestContext>();

        builder.UseBlueTusk(dataSource).UseBlueTusk(connection);
        using (var context = new TestContext(builder.Options))
        {
            Assert.Same(connection, context.Database.GetDbConnection());
        }

        builder.UseBlueTusk(dataSource);
        using (var context = new TestContext(builder.Options))
        {
            Assert.NotSame(connection, context.Database.GetDbConnection());
        }

        var replacement = $"{ConnectionString};Application Name=overload-switch";
        builder.UseBlueTusk(replacement);
        using (var context = new TestContext(builder.Options))
        {
            Assert.Equal(replacement, context.Database.GetConnectionString());
            Assert.NotSame(connection, context.Database.GetDbConnection());
        }
    }

    [Fact]
    public void Context_owns_its_data_source_connection_but_not_the_data_source()
    {
        using var dataSource = BlueTuskDataSource.Create(ConnectionString);
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseBlueTusk(dataSource)
            .Options;
        DbConnection contextConnection;

        using (var context = new TestContext(options))
        {
            contextConnection = context.Database.GetDbConnection();
        }

        Assert.Throws<ObjectDisposedException>(() => contextConnection.Open());
        using var sourceConnection = dataSource.CreateConnection();
        Assert.NotSame(contextConnection, sourceConnection);
    }

    [Fact]
    public void Data_source_configuration_reuses_provider_services_and_exposes_debug_metadata()
    {
        using var firstDataSource = BlueTuskDataSource.Create(ConnectionString);
        using var secondDataSource = BlueTuskDataSource.Create(ConnectionString);
        var first = new DbContextOptionsBuilder<TestContext>()
            .UseBlueTusk(firstDataSource)
            .Options.Extensions.Single(extension => extension.Info.LogFragment.Contains("BlueTusk", StringComparison.Ordinal));
        var second = new DbContextOptionsBuilder<TestContext>()
            .UseBlueTusk(secondDataSource)
            .Options.Extensions.Single(extension => extension.Info.LogFragment.Contains("BlueTusk", StringComparison.Ordinal));
        var debugInfo = new Dictionary<string, string>();

        first.Info.PopulateDebugInfo(debugInfo);

        Assert.Equal(0, first.Info.GetServiceProviderHashCode());
        Assert.True(first.Info.ShouldUseSameServiceProvider(second.Info));
        Assert.Equal("1", debugInfo["BlueTusk"]);
        Assert.Equal("1", debugInfo["BlueTusk:DataSource"]);
        Assert.Contains("data source", first.Info.LogFragment, StringComparison.Ordinal);
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

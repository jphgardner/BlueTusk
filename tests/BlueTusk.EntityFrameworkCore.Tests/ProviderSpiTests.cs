using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BlueTusk.Data;
using BlueTusk.Data.Internal;
using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace BlueTusk.EntityFrameworkCore.Tests;

#pragma warning disable EF1001 // Tests intentionally enforce the provider's internal EF/Data boundary.

public sealed class ProviderSpiTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=secret;Database=bluetusk_tests";

    [Fact]
    public void Provider_SPI_is_internal_and_registered_as_one_stable_service()
    {
        Assert.False(typeof(IProviderServices).IsVisible);
        Assert.False(typeof(IProviderConnection).IsVisible);
        Assert.False(typeof(IProviderDataSource).IsVisible);
        Assert.False(typeof(DatabaseLifecycleSettings).IsVisible);
        Assert.False(typeof(ProviderCapabilities).IsVisible);
        Assert.False(typeof(ProviderServices).IsVisible);

        var services = new ServiceCollection();
        services.AddEntityFrameworkBlueTusk();
        using var provider = services.BuildServiceProvider();

        Assert.Same(
            ProviderServices.Instance,
            provider.GetRequiredService<IProviderServices>());
    }

    [Fact]
    public void Provider_SPI_covers_sources_connections_catalogue_capabilities_admin_and_diagnostics()
    {
        var services = ProviderServices.Instance;
        using var createdSource = services.CreateDataSource(ConnectionString);
        var source = services.GetDataSource(createdSource);

        Assert.Same(createdSource, source.Instance);
        Assert.Equal(ConnectionString, source.UnredactedConnectionString);

        using var sourceConnection = source.CreateConnection();
        var connection = services.GetConnection(sourceConnection);

        Assert.Same(sourceConnection, connection.Instance);
        Assert.Same(source.TypeRegistry, connection.TypeRegistry);
        Assert.Same(source.Diagnostics, connection.Diagnostics);
        var connectionSettings =
            new BlueTuskConnectionStringBuilder(connection.UnredactedConnectionString);
        Assert.Equal("bluetusk_tests", connectionSettings.Database);
        Assert.Equal("secret", connectionSettings.Password);
        Assert.Null(connection.Capabilities);

        using var adminConnection = source.CreateAdminConnection(
            "Host=localhost;Username=postgres;Password=admin-secret;Database=postgres");
        var admin = services.GetConnection(adminConnection);
        Assert.Equal("postgres", admin.Instance.Database);
        Assert.Equal(
            "admin-secret",
            new BlueTuskConnectionStringBuilder(admin.UnredactedConnectionString).Password);
        Assert.DoesNotContain(
            "admin-secret",
            admin.Instance.ConnectionString,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_SPI_rejects_foreign_connections_and_data_sources()
    {
        var services = ProviderServices.Instance;
        using var connection = new ForeignConnection();
        using var dataSource = new ForeignDataSource();

        var connectionError = Assert.Throws<InvalidOperationException>(
            () => services.GetConnection(connection));
        var sourceError = Assert.Throws<InvalidOperationException>(
            () => services.GetDataSource(dataSource));

        Assert.Contains(typeof(ForeignConnection).FullName!, connectionError.Message);
        Assert.Contains(typeof(ForeignDataSource).FullName!, sourceError.Message);
    }

    [Fact]
    public void EF_internals_use_the_provider_SPI_instead_of_concrete_transport_types()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "src", "BlueTusk.EntityFrameworkCore");
        var publicConfigurationBoundary = Path.GetFullPath(Path.Combine(
            sourceRoot,
            "Extensions",
            "BlueTuskDbContextOptionsBuilderExtensions.cs"));
        var forbiddenSyntax = new[]
        {
            "new BlueTuskConnection(",
            "(BlueTuskConnection)",
            " is BlueTuskConnection",
            " as BlueTuskConnection",
            "BlueTuskConnectionStringBuilder",
            "BlueTuskDataSource?",
            ".CreateUnpooledConnection(",
        };
        var violations = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetFullPath(path),
                publicConfigurationBoundary,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (Path: path, Line: line, Number: index + 1)))
            .Where(candidate => forbiddenSyntax.Any(candidate.Line.Contains))
            .Select(candidate =>
                $"{Path.GetRelativePath(repositoryRoot, candidate.Path)}:{candidate.Number}")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Concrete EF/Data coupling was found at {string.Join(", ", violations)}.");

        Assert.Equal(
            typeof(IProviderDataSource),
            typeof(BlueTuskOptionsExtension)
                .GetProperty(
                    nameof(BlueTuskOptionsExtension.DataSource),
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .PropertyType);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlueTusk.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the BlueTusk repository root.");
    }

    private sealed class ForeignConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) =>
            throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() =>
            throw new NotSupportedException();
    }

    private sealed class ForeignDataSource : DbDataSource
    {
        public override string ConnectionString => string.Empty;

        protected override DbConnection CreateDbConnection() => new ForeignConnection();
    }
}

#pragma warning restore EF1001

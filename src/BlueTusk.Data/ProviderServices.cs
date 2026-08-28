using System.Data.Common;
using BlueTusk.Diagnostics;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Internal;

/// <summary>
/// Minimal internal contract consumed by the EF provider. It is deliberately
/// assembly-internal until a third-party provider requirement is demonstrated.
/// </summary>
internal interface IProviderServices
{
    DbConnection CreateConnection(string connectionString);

    DbDataSource CreateDataSource(string connectionString);

    IProviderConnection GetConnection(DbConnection connection);

    IProviderDataSource GetDataSource(DbDataSource dataSource);

    DatabaseLifecycleSettings CreateDatabaseLifecycleSettings(
        string connectionString,
        string? configuredAdminDatabase = null);
}

internal interface IProviderConnection
{
    DbConnection Instance { get; }

    string UnredactedConnectionString { get; }

    BlueTuskTypeRegistry TypeRegistry { get; }

    ProviderCapabilities? Capabilities { get; }

    BlueTuskDiagnosticsOptions Diagnostics { get; }

    DbConnection CreateAdminConnection(string connectionString);

    void AllowPendingPoolResetOnOpen();

    void UseBufferedDataReaders();

    void PreferExtendedProtocol();

    void PreferBinaryResults();

    void ReloadTypes();

    ValueTask ReloadTypesAsync(CancellationToken cancellationToken = default);
}

internal interface IProviderDataSource
{
    DbDataSource Instance { get; }

    string UnredactedConnectionString { get; }

    BlueTuskTypeRegistry TypeRegistry { get; }

    BlueTuskDiagnosticsOptions Diagnostics { get; }

    DbConnection CreateConnection();

    DbConnection CreateAdminConnection(string connectionString);

    void ClearPool();

    ValueTask ClearPoolAsync();
}

internal sealed record DatabaseLifecycleSettings(
    string TargetDatabase,
    string AdminConnectionString);

internal readonly record struct ProviderCapabilities(bool SupportsSqlPgq);

internal sealed class ProviderServices : IProviderServices
{
    public static IProviderServices Instance { get; } = new ProviderServices();

    private ProviderServices()
    {
    }

    public DbConnection CreateConnection(string connectionString) =>
        new BlueTuskConnection(
            connectionString ??
            throw new ArgumentNullException(nameof(connectionString)));

    public DbDataSource CreateDataSource(string connectionString) =>
        BlueTuskDataSource.Create(
            connectionString ??
            throw new ArgumentNullException(nameof(connectionString)));

    public IProviderConnection GetConnection(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection as IProviderConnection ??
            throw new InvalidOperationException(
                $"The EF provider requires a BlueTusk provider connection, not '{connection.GetType().FullName}'.");
    }

    public IProviderDataSource GetDataSource(DbDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return dataSource as IProviderDataSource ??
            throw new InvalidOperationException(
                $"The EF provider requires a BlueTusk provider data source, not '{dataSource.GetType().FullName}'.");
    }

    public DatabaseLifecycleSettings CreateDatabaseLifecycleSettings(
        string connectionString,
        string? configuredAdminDatabase = null)
    {
        var settings = new BlueTuskConnectionStringBuilder(connectionString);
        var targetDatabase = settings.Database;
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDatabase);
        var adminDatabase = configuredAdminDatabase ??
            (string.Equals(targetDatabase, "postgres", StringComparison.Ordinal)
                ? "template1"
                : "postgres");
        ArgumentException.ThrowIfNullOrWhiteSpace(adminDatabase);
        if (string.Equals(targetDatabase, adminDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The BlueTusk admin database must differ from the target database.");
        }

        settings.Database = adminDatabase;
        settings.Pooling = false;
        settings.TargetSessionAttributes =
            BlueTuskTargetSessionAttributes.ReadWrite;
        return new DatabaseLifecycleSettings(
            targetDatabase,
            settings.ConnectionString);
    }
}

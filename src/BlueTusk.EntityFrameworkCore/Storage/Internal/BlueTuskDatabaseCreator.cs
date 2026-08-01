using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskDatabaseCreator(RelationalDatabaseCreatorDependencies dependencies)
    : RelationalDatabaseCreator(dependencies)
{
    public override bool Exists()
    {
        try
        {
            Dependencies.Connection.Open(errorsExpected: true);
            Dependencies.Connection.Close();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Dependencies.Connection.OpenAsync(
                cancellationToken,
                errorsExpected: true).ConfigureAwait(false);
            await Dependencies.Connection.CloseAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException &&
            !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public override void Create()
    {
        var lifecycle = CreateLifecycleSettings();
        PrepareTargetDataSource();
        using var connection = CreateAdminConnection(lifecycle.AdminConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {DelimitIdentifier(lifecycle.TargetDatabase)}";
        _ = command.ExecuteNonQuery();
        ReloadTargetTypes();
    }

    public override async Task CreateAsync(CancellationToken cancellationToken = default)
    {
        var lifecycle = CreateLifecycleSettings();
        await PrepareTargetDataSourceAsync().ConfigureAwait(false);
        await using var connection = CreateAdminConnection(lifecycle.AdminConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {DelimitIdentifier(lifecycle.TargetDatabase)}";
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await ReloadTargetTypesAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Delete()
    {
        var lifecycle = CreateLifecycleSettings();
        PrepareTargetDataSource();
        using var connection = CreateAdminConnection(lifecycle.AdminConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE {DelimitIdentifier(lifecycle.TargetDatabase)} WITH (FORCE)";
        _ = command.ExecuteNonQuery();
    }

    public override async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var lifecycle = CreateLifecycleSettings();
        await PrepareTargetDataSourceAsync().ConfigureAwait(false);
        await using var connection = CreateAdminConnection(lifecycle.AdminConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE {DelimitIdentifier(lifecycle.TargetDatabase)} WITH (FORCE)";
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override bool HasTables()
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS c
                JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p')
                  AND n.nspname NOT IN ('pg_catalog', 'information_schema'))
            """;

        Dependencies.Connection.Open();
        try
        {
            using var command = Dependencies.Connection.DbConnection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar() is true;
        }
        finally
        {
            Dependencies.Connection.Close();
        }
    }

    public override async Task<bool> HasTablesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS c
                JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p')
                  AND n.nspname NOT IN ('pg_catalog', 'information_schema'))
            """;

        await Dependencies.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Dependencies.Connection.DbConnection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }
        finally
        {
            await Dependencies.Connection.CloseAsync().ConfigureAwait(false);
        }
    }

    internal static BlueTuskDatabaseLifecycleSettings CreateLifecycleSettings(
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
        settings.TargetSessionAttributes = BlueTuskTargetSessionAttributes.ReadWrite;
        return new BlueTuskDatabaseLifecycleSettings(targetDatabase, settings.ConnectionString);
    }

    private BlueTuskDatabaseLifecycleSettings CreateLifecycleSettings()
    {
        var options = Dependencies.ContextOptions.FindExtension<BlueTuskOptionsExtension>();
        return CreateLifecycleSettings(
            Dependencies.Connection.ConnectionString ?? string.Empty,
            options?.AdminDatabase);
    }

    private BlueTuskConnection CreateAdminConnection(string connectionString) =>
        GetDataSource()?.CreateUnpooledConnection(connectionString)
        ?? new BlueTuskConnection(connectionString);

    private string DelimitIdentifier(string identifier) =>
        Dependencies.SqlGenerationHelper.DelimitIdentifier(identifier);

    private void PrepareTargetDataSource()
    {
        Dependencies.Connection.Close();
        GetDataSource()?.ClearPool();
    }

    private async Task PrepareTargetDataSourceAsync()
    {
        await Dependencies.Connection.CloseAsync().ConfigureAwait(false);
        if (GetDataSource() is { } dataSource)
        {
            await dataSource.ClearPoolAsync().ConfigureAwait(false);
        }
    }

    private void ReloadTargetTypes()
    {
        Dependencies.Connection.Open();
        try
        {
            ((BlueTuskConnection)Dependencies.Connection.DbConnection).ReloadTypes();
        }
        finally
        {
            Dependencies.Connection.Close();
        }
    }

    private async Task ReloadTargetTypesAsync(CancellationToken cancellationToken)
    {
        await Dependencies.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ((BlueTuskConnection)Dependencies.Connection.DbConnection)
                .ReloadTypesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await Dependencies.Connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private BlueTuskDataSource? GetDataSource() =>
        (Dependencies.Connection as BlueTuskRelationalConnection)?.DataSource;
}

internal sealed record BlueTuskDatabaseLifecycleSettings(
    string TargetDatabase,
    string AdminConnectionString);

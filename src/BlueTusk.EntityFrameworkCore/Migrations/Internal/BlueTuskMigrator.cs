using BlueTusk.Data.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

#pragma warning disable EF1001 // Migrator is EF Core's internal migrator, extended as Npgsql does.

/// <summary>
/// Reloads the PostgreSQL type catalogue after migrations run. Migrations commonly create the
/// enum, composite, domain, and range types that a data source maps with <c>MapEnum</c> or
/// <c>MapComposite</c>; those mappings stay unresolved until the catalogue is read again.
/// </summary>
internal sealed class BlueTuskMigrator(
    IMigrationsAssembly migrationsAssembly,
    IHistoryRepository historyRepository,
    IDatabaseCreator databaseCreator,
    IMigrationsSqlGenerator migrationsSqlGenerator,
    IRawSqlCommandBuilder rawSqlCommandBuilder,
    IMigrationCommandExecutor migrationCommandExecutor,
    IRelationalConnection connection,
    ISqlGenerationHelper sqlGenerationHelper,
    ICurrentDbContext currentContext,
    IModelRuntimeInitializer modelRuntimeInitializer,
    IDiagnosticsLogger<DbLoggerCategory.Migrations> logger,
    IRelationalCommandDiagnosticsLogger commandLogger,
    IDatabaseProvider databaseProvider,
    IMigrationsModelDiffer migrationsModelDiffer,
    IDesignTimeModel designTimeModel,
    IDbContextOptions contextOptions,
    IExecutionStrategy executionStrategy,
    IProviderServices providerServices)
    : Migrator(
        migrationsAssembly,
        historyRepository,
        databaseCreator,
        migrationsSqlGenerator,
        rawSqlCommandBuilder,
        migrationCommandExecutor,
        connection,
        sqlGenerationHelper,
        currentContext,
        modelRuntimeInitializer,
        logger,
        commandLogger,
        databaseProvider,
        migrationsModelDiffer,
        designTimeModel,
        contextOptions,
        executionStrategy)
{
    // Kept explicitly: the base Migrator also receives the connection.
    private readonly IRelationalConnection _connection = connection;

    public override void Migrate(string? targetMigration)
    {
        base.Migrate(targetMigration);
        _connection.Open();
        try
        {
            providerServices.GetConnection(_connection.DbConnection).ReloadTypes();
        }
        finally
        {
            _connection.Close();
        }
    }

    public override async Task MigrateAsync(
        string? targetMigration,
        CancellationToken cancellationToken = default)
    {
        await base.MigrateAsync(targetMigration, cancellationToken).ConfigureAwait(false);
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await providerServices.GetConnection(_connection.DbConnection)
                .ReloadTypesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _connection.CloseAsync().ConfigureAwait(false);
        }
    }
}

#pragma warning restore EF1001

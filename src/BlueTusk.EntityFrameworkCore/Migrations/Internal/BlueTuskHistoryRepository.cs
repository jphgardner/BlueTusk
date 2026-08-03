using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

#pragma warning disable EF1001 // Provider services necessarily consume EF Core infrastructure APIs.

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal sealed class BlueTuskHistoryRepository(HistoryRepositoryDependencies dependencies)
    : HistoryRepository(dependencies)
{
    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Transaction;

    protected override string ExistsSql
    {
        get
        {
            var identifier = SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema)
                .Replace("'", "''", StringComparison.Ordinal);
            return $"SELECT to_regclass('{identifier}') IS NOT NULL";
        }
    }

    public override IMigrationsDatabaseLock AcquireDatabaseLock()
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();
        Dependencies.RawSqlCommandBuilder
            .Build($"LOCK TABLE {SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema)} IN ACCESS EXCLUSIVE MODE")
            .ExecuteNonQuery(CreateCommandParameters());
        return new BlueTuskMigrationsDatabaseLock(this);
    }

    public override async Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(
        CancellationToken cancellationToken = default)
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();
        await Dependencies.RawSqlCommandBuilder
            .Build($"LOCK TABLE {SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema)} IN ACCESS EXCLUSIVE MODE")
            .ExecuteNonQueryAsync(CreateCommandParameters(), cancellationToken)
            .ConfigureAwait(false);
        return new BlueTuskMigrationsDatabaseLock(this);
    }

    protected override bool InterpretExistsResult(object? value) => value is true;

    public override string GetCreateIfNotExistsScript()
        => GetCreateScript().Replace("CREATE TABLE", "CREATE TABLE IF NOT EXISTS", StringComparison.Ordinal);

    public override string GetBeginIfNotExistsScript(string migrationId)
        => GetBeginConditionalScript(migrationId, negate: true);

    public override string GetBeginIfExistsScript(string migrationId)
        => GetBeginConditionalScript(migrationId, negate: false);

    public override string GetEndIfScript() => "END IF;\nEND $EF$;" + Environment.NewLine;

    private string GetBeginConditionalScript(string migrationId, bool negate)
    {
        ArgumentException.ThrowIfNullOrEmpty(migrationId);
        var escapedMigrationId = migrationId.Replace("'", "''", StringComparison.Ordinal);
        var condition = negate ? "NOT EXISTS" : "EXISTS";
        return $"DO $EF$\nBEGIN\n    IF {condition} (SELECT 1 FROM {SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema)} WHERE {SqlGenerationHelper.DelimitIdentifier(MigrationIdColumnName)} = '{escapedMigrationId}') THEN" + Environment.NewLine;
    }

    private RelationalCommandParameterObject CreateCommandParameters()
        => new(
            Dependencies.Connection,
            parameterValues: null,
            readerColumns: null,
            Dependencies.CurrentContext.Context,
            Dependencies.CommandLogger,
            CommandSource.Migrations);

    private sealed class BlueTuskMigrationsDatabaseLock(IHistoryRepository historyRepository)
        : IMigrationsDatabaseLock
    {
        public IHistoryRepository HistoryRepository { get; } = historyRepository;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}

#pragma warning restore EF1001

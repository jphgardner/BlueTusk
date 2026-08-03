using BlueTusk.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations owned by the optional TimescaleDB EF package.</summary>
public static class BlueTuskTimescaleDbMigrationBuilderExtensions
{
    /// <summary>Creates the TimescaleDB extension if it is not installed.</summary>
    public static OperationBuilder<SqlOperation> EnsureTimescaleDb(
        this MigrationBuilder migrationBuilder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        return migrationBuilder.Sql(
            $"CREATE EXTENSION IF NOT EXISTS {BlueTuskSqlIdentifier.Delimit("timescaledb")} " +
            $"WITH SCHEMA {BlueTuskSqlIdentifier.Delimit(schema)}");
    }

    /// <summary>Drops the TimescaleDB extension if it is installed.</summary>
    public static OperationBuilder<SqlOperation> DropTimescaleDb(
        this MigrationBuilder migrationBuilder,
        bool cascade = false)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        return migrationBuilder.Sql(
            $"DROP EXTENSION IF EXISTS {BlueTuskSqlIdentifier.Delimit("timescaledb")}" +
            (cascade ? " CASCADE" : string.Empty));
    }

    /// <summary>Converts an existing table into an idempotent range hypertable.</summary>
    public static OperationBuilder<SqlOperation> ConvertToHypertable(
        this MigrationBuilder migrationBuilder,
        string table,
        string timeColumn,
        string schema = "public",
        string extensionSchema = "public",
        bool migrateData = false)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeColumn);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionSchema);

        var relation = QuoteLiteral(BlueTuskSqlIdentifier.Delimit(table, schema));
        var column = QuoteLiteral(timeColumn);
        var createHypertable = BlueTuskSqlIdentifier.Delimit("create_hypertable", extensionSchema);
        var byRange = BlueTuskSqlIdentifier.Delimit("by_range", extensionSchema);
        return migrationBuilder.Sql(
            $"SELECT * FROM {createHypertable}(" +
            $"{relation}::regclass, {byRange}({column}::name), " +
            $"if_not_exists => TRUE, migrate_data => {(migrateData ? "TRUE" : "FALSE")})");
    }

    private static string QuoteLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}

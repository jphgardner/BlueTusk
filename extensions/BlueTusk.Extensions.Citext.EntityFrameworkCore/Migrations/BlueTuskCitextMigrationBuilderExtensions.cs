using BlueTusk.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations owned by the optional citext EF package.</summary>
public static class BlueTuskCitextMigrationBuilderExtensions
{
    /// <summary>Creates PostgreSQL's citext extension if it is not installed.</summary>
    public static OperationBuilder<SqlOperation> EnsureBlueTuskCitext(
        this MigrationBuilder migrationBuilder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        return migrationBuilder.Sql(
            $"CREATE EXTENSION IF NOT EXISTS {BlueTuskSqlIdentifier.Delimit("citext")} " +
            $"WITH SCHEMA {BlueTuskSqlIdentifier.Delimit(schema)}");
    }

    /// <summary>Drops PostgreSQL's citext extension if it is installed.</summary>
    public static OperationBuilder<SqlOperation> DropBlueTuskCitext(
        this MigrationBuilder migrationBuilder,
        bool cascade = false)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        return migrationBuilder.Sql(
            $"DROP EXTENSION IF EXISTS {BlueTuskSqlIdentifier.Delimit("citext")}" +
            (cascade ? " CASCADE" : string.Empty));
    }
}

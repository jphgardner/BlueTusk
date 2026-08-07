using BlueTusk.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations owned by the optional pgvector EF package.</summary>
public static class BlueTuskPgVectorMigrationBuilderExtensions
{
    /// <summary>Creates PostgreSQL's vector extension if it is not installed.</summary>
    public static OperationBuilder<SqlOperation> EnsurePgVector(
        this MigrationBuilder migrationBuilder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        return migrationBuilder.Sql(
            $"CREATE EXTENSION IF NOT EXISTS {BlueTuskSqlIdentifier.Delimit("vector")} " +
            $"WITH SCHEMA {BlueTuskSqlIdentifier.Delimit(schema)}");
    }

    /// <summary>Drops PostgreSQL's vector extension if it is installed.</summary>
    public static OperationBuilder<SqlOperation> DropPgVector(
        this MigrationBuilder migrationBuilder,
        bool cascade = false)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        return migrationBuilder.Sql(
            $"DROP EXTENSION IF EXISTS {BlueTuskSqlIdentifier.Delimit("vector")}" +
            (cascade ? " CASCADE" : string.Empty));
    }
}

using BlueTusk.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations owned by the optional PostGIS EF package.</summary>
public static class BlueTuskPostGisMigrationBuilderExtensions
{
    /// <summary>Creates the PostGIS extension if it is not installed.</summary>
    public static OperationBuilder<SqlOperation> EnsureBlueTuskPostGis(
        this MigrationBuilder migrationBuilder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        return migrationBuilder.Sql(
            $"CREATE EXTENSION IF NOT EXISTS {BlueTuskSqlIdentifier.Delimit("postgis")} " +
            $"WITH SCHEMA {BlueTuskSqlIdentifier.Delimit(schema)}");
    }

    /// <summary>Drops the PostGIS extension if it is installed.</summary>
    public static OperationBuilder<SqlOperation> DropBlueTuskPostGis(
        this MigrationBuilder migrationBuilder,
        bool cascade = false)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        return migrationBuilder.Sql(
            $"DROP EXTENSION IF EXISTS {BlueTuskSqlIdentifier.Delimit("postgis")}" +
            (cascade ? " CASCADE" : string.Empty));
    }
}

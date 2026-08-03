using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration-builder extensions for PostgreSQL table inheritance.</summary>
public static class BlueTuskTableInheritanceMigrationBuilderExtensions
{
    /// <summary>Adds a direct table-inheritance parent.</summary>
    public static OperationBuilder<AddBlueTuskTableInheritanceOperation> AddBlueTuskTableInheritance(
        this MigrationBuilder migrationBuilder,
        string table,
        string parentTable,
        string? schema = null,
        string? parentSchema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentTable);
        var operation = new AddBlueTuskTableInheritanceOperation
        {
            Table = table,
            Schema = schema,
            ParentTable = parentTable,
            ParentSchema = parentSchema,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AddBlueTuskTableInheritanceOperation>(operation);
    }

    /// <summary>Removes a direct table-inheritance parent without dropping either table.</summary>
    public static OperationBuilder<RemoveBlueTuskTableInheritanceOperation> RemoveBlueTuskTableInheritance(
        this MigrationBuilder migrationBuilder,
        string table,
        string parentTable,
        string? schema = null,
        string? parentSchema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentTable);
        var operation = new RemoveBlueTuskTableInheritanceOperation
        {
            Table = table,
            Schema = schema,
            ParentTable = parentTable,
            ParentSchema = parentSchema,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RemoveBlueTuskTableInheritanceOperation>(operation);
    }
}

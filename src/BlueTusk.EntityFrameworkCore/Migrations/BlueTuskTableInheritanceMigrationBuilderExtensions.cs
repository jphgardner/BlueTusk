using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration-builder extensions for PostgreSQL table inheritance.</summary>
public static class BlueTuskTableInheritanceMigrationBuilderExtensions
{
    /// <summary>Adds a direct table-inheritance parent.</summary>
    public static OperationBuilder<AddTableInheritanceOperation> AddTableInheritance(
        this MigrationBuilder migrationBuilder,
        string table,
        string parentTable,
        string? schema = null,
        string? parentSchema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentTable);
        var operation = new AddTableInheritanceOperation
        {
            Table = table,
            Schema = schema,
            ParentTable = parentTable,
            ParentSchema = parentSchema,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AddTableInheritanceOperation>(operation);
    }

    /// <summary>Removes a direct table-inheritance parent without dropping either table.</summary>
    public static OperationBuilder<RemoveTableInheritanceOperation> RemoveTableInheritance(
        this MigrationBuilder migrationBuilder,
        string table,
        string parentTable,
        string? schema = null,
        string? parentSchema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentTable);
        var operation = new RemoveTableInheritanceOperation
        {
            Table = table,
            Schema = schema,
            ParentTable = parentTable,
            ParentSchema = parentSchema,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RemoveTableInheritanceOperation>(operation);
    }
}

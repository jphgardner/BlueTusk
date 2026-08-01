using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for PostgreSQL exclusion constraints.</summary>
public static class BlueTuskExclusionConstraintMigrationBuilderExtensions
{
    public static OperationBuilder<AddBlueTuskExclusionConstraintOperation> AddBlueTuskExclusionConstraint(
        this MigrationBuilder migrationBuilder,
        string table,
        BlueTuskExclusionConstraintDefinition definition,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        BlueTuskExclusionConstraintMetadata.Validate(definition);
        var operation = new AddBlueTuskExclusionConstraintOperation
        {
            Table = table,
            Schema = schema,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AddBlueTuskExclusionConstraintOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AddBlueTuskExclusionConstraintOperation> AddBlueTuskExclusionConstraint(
        this MigrationBuilder migrationBuilder,
        string table,
        string serializedDefinition,
        string? schema = null) =>
        AddBlueTuskExclusionConstraint(
            migrationBuilder,
            table,
            BlueTuskExclusionConstraintMetadata.DeserializeDefinition(serializedDefinition),
            schema);

    public static OperationBuilder<DropBlueTuskExclusionConstraintOperation> DropBlueTuskExclusionConstraint(
        this MigrationBuilder migrationBuilder,
        string table,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropBlueTuskExclusionConstraintOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskExclusionConstraintOperation>(operation);
    }

    public static OperationBuilder<RenameBlueTuskExclusionConstraintOperation> RenameBlueTuskExclusionConstraint(
        this MigrationBuilder migrationBuilder,
        string table,
        string name,
        string newName,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new RenameBlueTuskExclusionConstraintOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            NewName = newName,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameBlueTuskExclusionConstraintOperation>(operation);
    }
}

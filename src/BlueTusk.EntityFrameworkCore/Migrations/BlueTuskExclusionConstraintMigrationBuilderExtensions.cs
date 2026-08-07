using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for PostgreSQL exclusion constraints.</summary>
public static class BlueTuskExclusionConstraintMigrationBuilderExtensions
{
    public static OperationBuilder<AddExclusionConstraintOperation> AddExclusionConstraint(
        this MigrationBuilder migrationBuilder,
        string table,
        BlueTuskExclusionConstraintDefinition definition,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        BlueTuskExclusionConstraintMetadata.Validate(definition);
        var operation = new AddExclusionConstraintOperation
        {
            Table = table,
            Schema = schema,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AddExclusionConstraintOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AddExclusionConstraintOperation> AddExclusionConstraint(
        this MigrationBuilder migrationBuilder,
        string table,
        string serializedDefinition,
        string? schema = null) =>
        AddExclusionConstraint(
            migrationBuilder,
            table,
            BlueTuskExclusionConstraintMetadata.DeserializeDefinition(serializedDefinition),
            schema);

    public static OperationBuilder<DropExclusionConstraintOperation> DropExclusionConstraint(
        this MigrationBuilder migrationBuilder,
        string table,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropExclusionConstraintOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropExclusionConstraintOperation>(operation);
    }

    public static OperationBuilder<RenameExclusionConstraintOperation> RenameExclusionConstraint(
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
        var operation = new RenameExclusionConstraintOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            NewName = newName,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameExclusionConstraintOperation>(operation);
    }
}

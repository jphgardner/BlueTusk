using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.ExpressionIndexes;
using BlueTusk.EntityFrameworkCore.ExpressionIndexes.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for provider-owned PostgreSQL expression indexes.</summary>
public static class BlueTuskExpressionIndexMigrationBuilderExtensions
{
    public static OperationBuilder<CreateExpressionIndexOperation> CreateExpressionIndex(
        this MigrationBuilder migrationBuilder,
        string table,
        BlueTuskExpressionIndexDefinition definition,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        BlueTuskExpressionIndexMetadata.Validate(definition);
        var operation = new CreateExpressionIndexOperation
        {
            Table = table,
            Schema = schema,
            Definition = BlueTuskExpressionIndexMetadata.Normalize(definition),
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateExpressionIndexOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateExpressionIndexOperation> CreateExpressionIndex(
        this MigrationBuilder migrationBuilder,
        string table,
        string serializedDefinition,
        string? schema = null) =>
        CreateExpressionIndex(
            migrationBuilder,
            table,
            BlueTuskExpressionIndexMetadata.DeserializeDefinition(serializedDefinition),
            schema);

    public static OperationBuilder<DropExpressionIndexOperation> DropExpressionIndex(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null,
        bool concurrently = false)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropExpressionIndexOperation
        {
            Name = name,
            Schema = schema,
            Concurrently = concurrently,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropExpressionIndexOperation>(operation);
    }

    public static OperationBuilder<RenameExpressionIndexOperation> RenameExpressionIndex(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new RenameExpressionIndexOperation
        {
            Name = name,
            Schema = schema,
            NewName = newName,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameExpressionIndexOperation>(operation);
    }
}

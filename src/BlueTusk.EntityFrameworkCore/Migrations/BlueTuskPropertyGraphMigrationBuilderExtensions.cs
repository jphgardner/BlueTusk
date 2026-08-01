using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for PostgreSQL 19 property graphs.</summary>
public static class BlueTuskPropertyGraphMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskPropertyGraphOperation> CreateBlueTuskPropertyGraph(
        this MigrationBuilder migrationBuilder,
        BlueTuskPropertyGraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentNullException.ThrowIfNull(definition);
        var operation = new CreateBlueTuskPropertyGraphOperation { Definition = definition };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateBlueTuskPropertyGraphOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskPropertyGraphOperation> CreateBlueTuskPropertyGraph(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateBlueTuskPropertyGraph(
            migrationBuilder,
            BlueTuskPropertyGraphMetadata.Deserialize(serializedDefinition));

    public static OperationBuilder<DropBlueTuskPropertyGraphOperation> DropBlueTuskPropertyGraph(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropBlueTuskPropertyGraphOperation { Name = name, Schema = schema };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskPropertyGraphOperation>(operation);
    }

    public static OperationBuilder<AlterBlueTuskPropertyGraphOperation> AlterBlueTuskPropertyGraph(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName,
        string? schema = null,
        string? newSchema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new AlterBlueTuskPropertyGraphOperation
        {
            Name = name,
            Schema = schema,
            NewName = newName,
            NewSchema = newSchema,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskPropertyGraphOperation>(operation);
    }
}

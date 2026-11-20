using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Builders;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for PostgreSQL 19 property graphs.</summary>
public static class BlueTuskPropertyGraphMigrationBuilderExtensions
{
    public static OperationBuilder<CreatePropertyGraphOperation> CreatePropertyGraph(
        this MigrationBuilder migrationBuilder,
        string name,
        Action<BlueTuskPropertyGraphMigrationBuilder> configure,
        string? schema = null)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        BlueTuskGuard.NotNull(configure);
        var builder = new BlueTuskPropertyGraphMigrationBuilder(name, schema);
        configure(builder);
        return CreatePropertyGraph(migrationBuilder, builder.Build());
    }

    public static OperationBuilder<CreatePropertyGraphOperation> CreatePropertyGraph(
        this MigrationBuilder migrationBuilder,
        BlueTuskPropertyGraphDefinition definition)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskGuard.NotNull(definition);
        var operation = new CreatePropertyGraphOperation { Definition = definition };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreatePropertyGraphOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreatePropertyGraphOperation> CreatePropertyGraph(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreatePropertyGraph(
            migrationBuilder,
            BlueTuskPropertyGraphMetadata.Deserialize(serializedDefinition));

    public static OperationBuilder<DropPropertyGraphOperation> DropPropertyGraph(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        var operation = new DropPropertyGraphOperation { Name = name, Schema = schema };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropPropertyGraphOperation>(operation);
    }

    public static OperationBuilder<AlterPropertyGraphOperation> AlterPropertyGraph(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName,
        string? schema = null,
        string? newSchema = null)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        BlueTuskGuard.NotNullOrWhiteSpace(newName);
        var operation = new AlterPropertyGraphOperation
        {
            Name = name,
            Schema = schema,
            NewName = newName,
            NewSchema = newSchema,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterPropertyGraphOperation>(operation);
    }
}

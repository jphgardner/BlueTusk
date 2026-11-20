using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Tablespaces;
using BlueTusk.EntityFrameworkCore.Tablespaces.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for PostgreSQL cluster-wide tablespaces.</summary>
public static class BlueTuskTablespaceMigrationBuilderExtensions
{
    public static OperationBuilder<CreateTablespaceOperation> CreateTablespace(
        this MigrationBuilder builder,
        BlueTuskTablespaceDefinition definition)
    {
        BlueTuskGuard.NotNull(builder);
        BlueTuskTablespaceMetadata.Validate(definition);
        var operation = new CreateTablespaceOperation
        {
            Definition = BlueTuskTablespaceMetadata.Normalize(definition),
        };
        builder.Operations.Add(operation);
        return new OperationBuilder<CreateTablespaceOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateTablespaceOperation> CreateTablespace(
        this MigrationBuilder builder,
        string serializedDefinition) =>
        CreateTablespace(builder, BlueTuskTablespaceMetadata.DeserializeDefinition(serializedDefinition));

    public static OperationBuilder<AlterTablespaceOperation> AlterTablespace(
        this MigrationBuilder builder,
        BlueTuskTablespaceDefinition definition,
        BlueTuskTablespaceDefinition oldDefinition)
    {
        BlueTuskGuard.NotNull(builder);
        BlueTuskTablespaceMetadata.Validate(definition);
        BlueTuskTablespaceMetadata.Validate(oldDefinition);
        if (!BlueTuskTablespaceMetadata.LocationEquals(definition, oldDefinition))
        {
            throw new ArgumentException("PostgreSQL tablespace locations cannot be changed in place.",
                nameof(definition));
        }

        var operation = new AlterTablespaceOperation
        {
            Definition = BlueTuskTablespaceMetadata.Normalize(definition),
            OldDefinition = BlueTuskTablespaceMetadata.Normalize(oldDefinition),
        };
        builder.Operations.Add(operation);
        return new OperationBuilder<AlterTablespaceOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterTablespaceOperation> AlterTablespace(
        this MigrationBuilder builder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        AlterTablespace(
            builder,
            BlueTuskTablespaceMetadata.DeserializeDefinition(serializedDefinition),
            BlueTuskTablespaceMetadata.DeserializeDefinition(serializedOldDefinition));

    public static OperationBuilder<RenameTablespaceOperation> RenameTablespace(
        this MigrationBuilder builder,
        string name,
        string newName)
    {
        BlueTuskGuard.NotNull(builder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        BlueTuskGuard.NotNullOrWhiteSpace(newName);
        var operation = new RenameTablespaceOperation { Name = name, NewName = newName };
        builder.Operations.Add(operation);
        return new OperationBuilder<RenameTablespaceOperation>(operation);
    }

    public static OperationBuilder<DropTablespaceOperation> DropTablespace(
        this MigrationBuilder builder,
        string name,
        bool ifExists = false)
    {
        BlueTuskGuard.NotNull(builder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        var operation = new DropTablespaceOperation
        {
            Name = name,
            IfExists = ifExists,
            IsDestructiveChange = true,
        };
        builder.Operations.Add(operation);
        return new OperationBuilder<DropTablespaceOperation>(operation);
    }
}

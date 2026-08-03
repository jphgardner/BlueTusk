using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Tablespaces;
using BlueTusk.EntityFrameworkCore.Tablespaces.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for PostgreSQL cluster-wide tablespaces.</summary>
public static class BlueTuskTablespaceMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskTablespaceOperation> CreateBlueTuskTablespace(
        this MigrationBuilder builder,
        BlueTuskTablespaceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(builder);
        BlueTuskTablespaceMetadata.Validate(definition);
        var operation = new CreateBlueTuskTablespaceOperation
        {
            Definition = BlueTuskTablespaceMetadata.Normalize(definition),
        };
        builder.Operations.Add(operation);
        return new OperationBuilder<CreateBlueTuskTablespaceOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskTablespaceOperation> CreateBlueTuskTablespace(
        this MigrationBuilder builder,
        string serializedDefinition) =>
        CreateBlueTuskTablespace(builder, BlueTuskTablespaceMetadata.DeserializeDefinition(serializedDefinition));

    public static OperationBuilder<AlterBlueTuskTablespaceOperation> AlterBlueTuskTablespace(
        this MigrationBuilder builder,
        BlueTuskTablespaceDefinition definition,
        BlueTuskTablespaceDefinition oldDefinition)
    {
        ArgumentNullException.ThrowIfNull(builder);
        BlueTuskTablespaceMetadata.Validate(definition);
        BlueTuskTablespaceMetadata.Validate(oldDefinition);
        if (!BlueTuskTablespaceMetadata.LocationEquals(definition, oldDefinition))
        {
            throw new ArgumentException("PostgreSQL tablespace locations cannot be changed in place.",
                nameof(definition));
        }

        var operation = new AlterBlueTuskTablespaceOperation
        {
            Definition = BlueTuskTablespaceMetadata.Normalize(definition),
            OldDefinition = BlueTuskTablespaceMetadata.Normalize(oldDefinition),
        };
        builder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskTablespaceOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterBlueTuskTablespaceOperation> AlterBlueTuskTablespace(
        this MigrationBuilder builder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        AlterBlueTuskTablespace(
            builder,
            BlueTuskTablespaceMetadata.DeserializeDefinition(serializedDefinition),
            BlueTuskTablespaceMetadata.DeserializeDefinition(serializedOldDefinition));

    public static OperationBuilder<RenameBlueTuskTablespaceOperation> RenameBlueTuskTablespace(
        this MigrationBuilder builder,
        string name,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new RenameBlueTuskTablespaceOperation { Name = name, NewName = newName };
        builder.Operations.Add(operation);
        return new OperationBuilder<RenameBlueTuskTablespaceOperation>(operation);
    }

    public static OperationBuilder<DropBlueTuskTablespaceOperation> DropBlueTuskTablespace(
        this MigrationBuilder builder,
        string name,
        bool ifExists = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropBlueTuskTablespaceOperation
        {
            Name = name,
            IfExists = ifExists,
            IsDestructiveChange = true,
        };
        builder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskTablespaceOperation>(operation);
    }
}

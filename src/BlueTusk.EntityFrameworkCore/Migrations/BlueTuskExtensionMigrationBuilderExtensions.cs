using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Extensions;
using BlueTusk.EntityFrameworkCore.Extensions.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration-builder extensions for PostgreSQL extension installations.</summary>
public static class BlueTuskExtensionMigrationBuilderExtensions
{
    public static OperationBuilder<CreateExtensionOperation> CreateExtension(
        this MigrationBuilder migrationBuilder,
        BlueTuskExtensionDefinition definition,
        bool ifNotExists = false)
    {
        BlueTuskExtensionMetadata.Validate(definition);
        return Add(migrationBuilder, new CreateExtensionOperation
        {
            Definition = BlueTuskExtensionMetadata.Normalize(definition),
            IfNotExists = ifNotExists,
        });
    }

    public static OperationBuilder<AlterExtensionOperation> AlterExtension(
        this MigrationBuilder migrationBuilder,
        BlueTuskExtensionDefinition definition,
        BlueTuskExtensionDefinition oldDefinition)
    {
        BlueTuskExtensionMetadata.Validate(definition);
        BlueTuskExtensionMetadata.Validate(oldDefinition);
        if (!string.Equals(oldDefinition.Name, definition.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PostgreSQL cannot rename an extension. Use an explicit create/drop migration.");
        }

        return Add(migrationBuilder, new AlterExtensionOperation
        {
            Definition = BlueTuskExtensionMetadata.Normalize(definition),
            OldDefinition = BlueTuskExtensionMetadata.Normalize(oldDefinition),
        });
    }

    public static OperationBuilder<DropExtensionOperation> DropExtension(
        this MigrationBuilder migrationBuilder,
        string name,
        bool ifExists = false,
        bool cascade = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Add(migrationBuilder, new DropExtensionOperation
        {
            Name = name,
            IfExists = ifExists,
            Cascade = cascade,
            IsDestructiveChange = true,
        });
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateExtensionOperation> CreateExtension(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        bool ifNotExists = false) =>
        CreateExtension(
            migrationBuilder,
            BlueTuskExtensionMetadata.DeserializeDefinition(serializedDefinition),
            ifNotExists);

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterExtensionOperation> AlterExtension(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        AlterExtension(
            migrationBuilder,
            BlueTuskExtensionMetadata.DeserializeDefinition(serializedDefinition),
            BlueTuskExtensionMetadata.DeserializeDefinition(serializedOldDefinition));

    private static OperationBuilder<TOperation> Add<TOperation>(
        MigrationBuilder migrationBuilder,
        TOperation operation)
        where TOperation : MigrationOperation
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentNullException.ThrowIfNull(operation);
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<TOperation>(operation);
    }
}

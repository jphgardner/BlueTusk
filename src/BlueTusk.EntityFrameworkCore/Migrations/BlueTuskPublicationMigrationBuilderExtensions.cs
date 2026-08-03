using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Publications;
using BlueTusk.EntityFrameworkCore.Publications.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

public static class BlueTuskPublicationMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskPublicationOperation> CreateBlueTuskPublication(
        this MigrationBuilder migrationBuilder,
        BlueTuskPublicationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskPublicationMetadata.Validate(definition);
        var operation = new CreateBlueTuskPublicationOperation { Definition = definition };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateBlueTuskPublicationOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskPublicationOperation> CreateBlueTuskPublication(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateBlueTuskPublication(migrationBuilder, BlueTuskPublicationMetadata.DeserializeDefinition(serializedDefinition));

    public static OperationBuilder<AlterBlueTuskPublicationOperation> AlterBlueTuskPublication(
        this MigrationBuilder migrationBuilder,
        BlueTuskPublicationDefinition oldDefinition,
        BlueTuskPublicationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskPublicationMetadata.Validate(oldDefinition);
        BlueTuskPublicationMetadata.Validate(definition);
        var operation = new AlterBlueTuskPublicationOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskPublicationOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterBlueTuskPublicationOperation> AlterBlueTuskPublication(
        this MigrationBuilder migrationBuilder,
        string serializedOldDefinition,
        string serializedDefinition) =>
        AlterBlueTuskPublication(
            migrationBuilder,
            BlueTuskPublicationMetadata.DeserializeDefinition(serializedOldDefinition),
            BlueTuskPublicationMetadata.DeserializeDefinition(serializedDefinition));

    public static OperationBuilder<DropBlueTuskPublicationOperation> DropBlueTuskPublication(
        this MigrationBuilder migrationBuilder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropBlueTuskPublicationOperation { Name = name, IsDestructiveChange = true };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskPublicationOperation>(operation);
    }

    public static OperationBuilder<RenameBlueTuskPublicationOperation> RenameBlueTuskPublication(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new RenameBlueTuskPublicationOperation { Name = name, NewName = newName };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameBlueTuskPublicationOperation>(operation);
    }
}

using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Publications;
using BlueTusk.EntityFrameworkCore.Publications.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

public static class BlueTuskPublicationMigrationBuilderExtensions
{
    public static OperationBuilder<CreatePublicationOperation> CreatePublication(
        this MigrationBuilder migrationBuilder,
        BlueTuskPublicationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskPublicationMetadata.Validate(definition);
        var operation = new CreatePublicationOperation { Definition = definition };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreatePublicationOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreatePublicationOperation> CreatePublication(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreatePublication(migrationBuilder, BlueTuskPublicationMetadata.DeserializeDefinition(serializedDefinition));

    public static OperationBuilder<AlterPublicationOperation> AlterPublication(
        this MigrationBuilder migrationBuilder,
        BlueTuskPublicationDefinition oldDefinition,
        BlueTuskPublicationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskPublicationMetadata.Validate(oldDefinition);
        BlueTuskPublicationMetadata.Validate(definition);
        var operation = new AlterPublicationOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterPublicationOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterPublicationOperation> AlterPublication(
        this MigrationBuilder migrationBuilder,
        string serializedOldDefinition,
        string serializedDefinition) =>
        AlterPublication(
            migrationBuilder,
            BlueTuskPublicationMetadata.DeserializeDefinition(serializedOldDefinition),
            BlueTuskPublicationMetadata.DeserializeDefinition(serializedDefinition));

    public static OperationBuilder<DropPublicationOperation> DropPublication(
        this MigrationBuilder migrationBuilder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropPublicationOperation { Name = name, IsDestructiveChange = true };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropPublicationOperation>(operation);
    }

    public static OperationBuilder<RenamePublicationOperation> RenamePublication(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new RenamePublicationOperation { Name = name, NewName = newName };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenamePublicationOperation>(operation);
    }
}

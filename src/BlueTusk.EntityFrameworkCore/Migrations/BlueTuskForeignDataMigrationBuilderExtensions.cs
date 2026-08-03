using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.ForeignData;
using BlueTusk.EntityFrameworkCore.ForeignData.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

public static class BlueTuskForeignDataMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskForeignDataWrapperOperation>
        CreateBlueTuskForeignDataWrapper(
            this MigrationBuilder migrationBuilder,
            BlueTuskForeignDataWrapperDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskForeignDataMetadata.Validate(definition);
        var operation = new CreateBlueTuskForeignDataWrapperOperation { Definition = definition };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateBlueTuskForeignDataWrapperOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskForeignDataWrapperOperation>
        CreateBlueTuskForeignDataWrapper(this MigrationBuilder migrationBuilder, string serializedDefinition) =>
        CreateBlueTuskForeignDataWrapper(
            migrationBuilder,
            BlueTuskForeignDataMetadata.DeserializeWrapper(serializedDefinition));

    public static OperationBuilder<AlterBlueTuskForeignDataWrapperOperation>
        AlterBlueTuskForeignDataWrapper(
            this MigrationBuilder migrationBuilder,
            BlueTuskForeignDataWrapperDefinition oldDefinition,
            BlueTuskForeignDataWrapperDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskForeignDataMetadata.Validate(oldDefinition);
        BlueTuskForeignDataMetadata.Validate(definition);
        var operation = new AlterBlueTuskForeignDataWrapperOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskForeignDataWrapperOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterBlueTuskForeignDataWrapperOperation>
        AlterBlueTuskForeignDataWrapper(
            this MigrationBuilder migrationBuilder,
            string serializedOldDefinition,
            string serializedDefinition) =>
        AlterBlueTuskForeignDataWrapper(
            migrationBuilder,
            BlueTuskForeignDataMetadata.DeserializeWrapper(serializedOldDefinition),
            BlueTuskForeignDataMetadata.DeserializeWrapper(serializedDefinition));

    public static OperationBuilder<DropBlueTuskForeignDataWrapperOperation> DropBlueTuskForeignDataWrapper(
        this MigrationBuilder migrationBuilder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropBlueTuskForeignDataWrapperOperation
        {
            Name = name,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskForeignDataWrapperOperation>(operation);
    }

    public static OperationBuilder<RenameBlueTuskForeignDataWrapperOperation> RenameBlueTuskForeignDataWrapper(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new RenameBlueTuskForeignDataWrapperOperation { Name = name, NewName = newName };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameBlueTuskForeignDataWrapperOperation>(operation);
    }

    public static OperationBuilder<CreateBlueTuskForeignServerOperation> CreateBlueTuskForeignServer(
        this MigrationBuilder migrationBuilder,
        BlueTuskForeignServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskForeignDataMetadata.Validate(definition);
        var operation = new CreateBlueTuskForeignServerOperation { Definition = definition };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateBlueTuskForeignServerOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskForeignServerOperation> CreateBlueTuskForeignServer(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateBlueTuskForeignServer(
            migrationBuilder,
            BlueTuskForeignDataMetadata.DeserializeServer(serializedDefinition));

    public static OperationBuilder<AlterBlueTuskForeignServerOperation> AlterBlueTuskForeignServer(
        this MigrationBuilder migrationBuilder,
        BlueTuskForeignServerDefinition oldDefinition,
        BlueTuskForeignServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskForeignDataMetadata.Validate(oldDefinition);
        BlueTuskForeignDataMetadata.Validate(definition);
        var operation = new AlterBlueTuskForeignServerOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskForeignServerOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterBlueTuskForeignServerOperation> AlterBlueTuskForeignServer(
        this MigrationBuilder migrationBuilder,
        string serializedOldDefinition,
        string serializedDefinition) =>
        AlterBlueTuskForeignServer(
            migrationBuilder,
            BlueTuskForeignDataMetadata.DeserializeServer(serializedOldDefinition),
            BlueTuskForeignDataMetadata.DeserializeServer(serializedDefinition));

    public static OperationBuilder<DropBlueTuskForeignServerOperation> DropBlueTuskForeignServer(
        this MigrationBuilder migrationBuilder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropBlueTuskForeignServerOperation
        {
            Name = name,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskForeignServerOperation>(operation);
    }

    public static OperationBuilder<RenameBlueTuskForeignServerOperation> RenameBlueTuskForeignServer(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new RenameBlueTuskForeignServerOperation { Name = name, NewName = newName };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameBlueTuskForeignServerOperation>(operation);
    }

    public static OperationBuilder<CreateBlueTuskUserMappingOperation> CreateBlueTuskUserMapping(
        this MigrationBuilder migrationBuilder,
        BlueTuskUserMappingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskForeignDataMetadata.Validate(definition);
        var operation = new CreateBlueTuskUserMappingOperation { Definition = definition };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateBlueTuskUserMappingOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskUserMappingOperation> CreateBlueTuskUserMapping(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateBlueTuskUserMapping(
            migrationBuilder,
            BlueTuskForeignDataMetadata.DeserializeUserMapping(serializedDefinition));

    public static OperationBuilder<AlterBlueTuskUserMappingOperation> AlterBlueTuskUserMapping(
        this MigrationBuilder migrationBuilder,
        BlueTuskUserMappingDefinition oldDefinition,
        BlueTuskUserMappingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskForeignDataMetadata.Validate(oldDefinition);
        BlueTuskForeignDataMetadata.Validate(definition);
        var operation = new AlterBlueTuskUserMappingOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskUserMappingOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterBlueTuskUserMappingOperation> AlterBlueTuskUserMapping(
        this MigrationBuilder migrationBuilder,
        string serializedOldDefinition,
        string serializedDefinition) =>
        AlterBlueTuskUserMapping(
            migrationBuilder,
            BlueTuskForeignDataMetadata.DeserializeUserMapping(serializedOldDefinition),
            BlueTuskForeignDataMetadata.DeserializeUserMapping(serializedDefinition));

    public static OperationBuilder<DropBlueTuskUserMappingOperation> DropBlueTuskUserMapping(
        this MigrationBuilder migrationBuilder,
        string serverName,
        string? userName = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        if (userName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        }

        var operation = new DropBlueTuskUserMappingOperation
        {
            ServerName = serverName,
            UserName = userName,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskUserMappingOperation>(operation);
    }
}

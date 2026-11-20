using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.ForeignData;
using BlueTusk.EntityFrameworkCore.ForeignData.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

public static class BlueTuskForeignDataMigrationBuilderExtensions
{
    public static OperationBuilder<CreateForeignDataWrapperOperation>
        CreateForeignDataWrapper(
            this MigrationBuilder migrationBuilder,
            BlueTuskForeignDataWrapperDefinition definition)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskForeignDataMetadata.Validate(definition);
        var operation = new CreateForeignDataWrapperOperation { Definition = definition };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateForeignDataWrapperOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateForeignDataWrapperOperation>
        CreateForeignDataWrapper(this MigrationBuilder migrationBuilder, string serializedDefinition) =>
        CreateForeignDataWrapper(
            migrationBuilder,
            BlueTuskForeignDataMetadata.DeserializeWrapper(serializedDefinition));

    public static OperationBuilder<AlterForeignDataWrapperOperation>
        AlterForeignDataWrapper(
            this MigrationBuilder migrationBuilder,
            BlueTuskForeignDataWrapperDefinition oldDefinition,
            BlueTuskForeignDataWrapperDefinition definition)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskForeignDataMetadata.Validate(oldDefinition);
        BlueTuskForeignDataMetadata.Validate(definition);
        var operation = new AlterForeignDataWrapperOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterForeignDataWrapperOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterForeignDataWrapperOperation>
        AlterForeignDataWrapper(
            this MigrationBuilder migrationBuilder,
            string serializedOldDefinition,
            string serializedDefinition) =>
        AlterForeignDataWrapper(
            migrationBuilder,
            BlueTuskForeignDataMetadata.DeserializeWrapper(serializedOldDefinition),
            BlueTuskForeignDataMetadata.DeserializeWrapper(serializedDefinition));

    public static OperationBuilder<DropForeignDataWrapperOperation> DropForeignDataWrapper(
        this MigrationBuilder migrationBuilder,
        string name)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        var operation = new DropForeignDataWrapperOperation
        {
            Name = name,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropForeignDataWrapperOperation>(operation);
    }

    public static OperationBuilder<RenameForeignDataWrapperOperation> RenameForeignDataWrapper(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        BlueTuskGuard.NotNullOrWhiteSpace(newName);
        var operation = new RenameForeignDataWrapperOperation { Name = name, NewName = newName };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameForeignDataWrapperOperation>(operation);
    }

    public static OperationBuilder<CreateForeignServerOperation> CreateForeignServer(
        this MigrationBuilder migrationBuilder,
        BlueTuskForeignServerDefinition definition)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskForeignDataMetadata.Validate(definition);
        var operation = new CreateForeignServerOperation { Definition = definition };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateForeignServerOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateForeignServerOperation> CreateForeignServer(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateForeignServer(
            migrationBuilder,
            BlueTuskForeignDataMetadata.DeserializeServer(serializedDefinition));

    public static OperationBuilder<AlterForeignServerOperation> AlterForeignServer(
        this MigrationBuilder migrationBuilder,
        BlueTuskForeignServerDefinition oldDefinition,
        BlueTuskForeignServerDefinition definition)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskForeignDataMetadata.Validate(oldDefinition);
        BlueTuskForeignDataMetadata.Validate(definition);
        var operation = new AlterForeignServerOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterForeignServerOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterForeignServerOperation> AlterForeignServer(
        this MigrationBuilder migrationBuilder,
        string serializedOldDefinition,
        string serializedDefinition) =>
        AlterForeignServer(
            migrationBuilder,
            BlueTuskForeignDataMetadata.DeserializeServer(serializedOldDefinition),
            BlueTuskForeignDataMetadata.DeserializeServer(serializedDefinition));

    public static OperationBuilder<DropForeignServerOperation> DropForeignServer(
        this MigrationBuilder migrationBuilder,
        string name)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        var operation = new DropForeignServerOperation
        {
            Name = name,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropForeignServerOperation>(operation);
    }

    public static OperationBuilder<RenameForeignServerOperation> RenameForeignServer(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        BlueTuskGuard.NotNullOrWhiteSpace(newName);
        var operation = new RenameForeignServerOperation { Name = name, NewName = newName };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameForeignServerOperation>(operation);
    }

    public static OperationBuilder<CreateUserMappingOperation> CreateUserMapping(
        this MigrationBuilder migrationBuilder,
        BlueTuskUserMappingDefinition definition)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskForeignDataMetadata.Validate(definition);
        var operation = new CreateUserMappingOperation { Definition = definition };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateUserMappingOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateUserMappingOperation> CreateUserMapping(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateUserMapping(
            migrationBuilder,
            BlueTuskForeignDataMetadata.DeserializeUserMapping(serializedDefinition));

    public static OperationBuilder<AlterUserMappingOperation> AlterUserMapping(
        this MigrationBuilder migrationBuilder,
        BlueTuskUserMappingDefinition oldDefinition,
        BlueTuskUserMappingDefinition definition)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskForeignDataMetadata.Validate(oldDefinition);
        BlueTuskForeignDataMetadata.Validate(definition);
        var operation = new AlterUserMappingOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterUserMappingOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterUserMappingOperation> AlterUserMapping(
        this MigrationBuilder migrationBuilder,
        string serializedOldDefinition,
        string serializedDefinition) =>
        AlterUserMapping(
            migrationBuilder,
            BlueTuskForeignDataMetadata.DeserializeUserMapping(serializedOldDefinition),
            BlueTuskForeignDataMetadata.DeserializeUserMapping(serializedDefinition));

    public static OperationBuilder<DropUserMappingOperation> DropUserMapping(
        this MigrationBuilder migrationBuilder,
        string serverName,
        string? userName = null)
    {
        BlueTuskGuard.NotNull(migrationBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(serverName);
        if (userName is not null)
        {
            BlueTuskGuard.NotNullOrWhiteSpace(userName);
        }

        var operation = new DropUserMappingOperation
        {
            ServerName = serverName,
            UserName = userName,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropUserMappingOperation>(operation);
    }
}

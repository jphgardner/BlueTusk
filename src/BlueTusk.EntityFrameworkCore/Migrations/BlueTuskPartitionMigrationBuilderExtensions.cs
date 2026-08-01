using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Partitioning;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for PostgreSQL declarative partitioning.</summary>
public static class BlueTuskPartitionMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskPartitionOperation> CreateBlueTuskPartition(
        this MigrationBuilder migrationBuilder,
        string parentName,
        BlueTuskPartitionDefinition definition,
        string? parentSchema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentName);
        ArgumentNullException.ThrowIfNull(definition);
        var operation = new CreateBlueTuskPartitionOperation
        {
            ParentName = parentName,
            ParentSchema = parentSchema,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateBlueTuskPartitionOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskPartitionOperation> CreateBlueTuskPartition(
        this MigrationBuilder migrationBuilder,
        string parentName,
        string serializedDefinition,
        string? parentSchema = null) =>
        CreateBlueTuskPartition(
            migrationBuilder,
            parentName,
            BlueTuskPartitionMetadata.DeserializePartition(serializedDefinition),
            parentSchema);

    public static OperationBuilder<DropBlueTuskPartitionOperation> DropBlueTuskPartition(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropBlueTuskPartitionOperation { Name = name, Schema = schema };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskPartitionOperation>(operation);
    }

    public static OperationBuilder<AlterBlueTuskPartitionOperation> AlterBlueTuskPartition(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName,
        string? schema = null,
        string? newSchema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new AlterBlueTuskPartitionOperation
        {
            Name = name,
            Schema = schema,
            NewName = newName,
            NewSchema = newSchema,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskPartitionOperation>(operation);
    }

    public static OperationBuilder<AttachBlueTuskPartitionOperation> AttachBlueTuskPartition(
        this MigrationBuilder migrationBuilder,
        string parentName,
        string partitionName,
        BlueTuskPartitionBound bound,
        string? parentSchema = null,
        string? partitionSchema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);
        ArgumentNullException.ThrowIfNull(bound);
        var operation = new AttachBlueTuskPartitionOperation
        {
            ParentName = parentName,
            ParentSchema = parentSchema,
            PartitionName = partitionName,
            PartitionSchema = partitionSchema,
            Bound = bound,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AttachBlueTuskPartitionOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AttachBlueTuskPartitionOperation> AttachBlueTuskPartition(
        this MigrationBuilder migrationBuilder,
        string parentName,
        string partitionName,
        string serializedBound,
        string? parentSchema = null,
        string? partitionSchema = null) =>
        AttachBlueTuskPartition(
            migrationBuilder,
            parentName,
            partitionName,
            BlueTuskPartitionMetadata.DeserializeBound(serializedBound),
            parentSchema,
            partitionSchema);

    public static OperationBuilder<DetachBlueTuskPartitionOperation> DetachBlueTuskPartition(
        this MigrationBuilder migrationBuilder,
        string parentName,
        string partitionName,
        BlueTuskPartitionDetachMode mode = BlueTuskPartitionDetachMode.Normal,
        string? parentSchema = null,
        string? partitionSchema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown partition detach mode.");
        }

        var operation = new DetachBlueTuskPartitionOperation
        {
            ParentName = parentName,
            ParentSchema = parentSchema,
            PartitionName = partitionName,
            PartitionSchema = partitionSchema,
            Mode = mode,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DetachBlueTuskPartitionOperation>(operation);
    }
}

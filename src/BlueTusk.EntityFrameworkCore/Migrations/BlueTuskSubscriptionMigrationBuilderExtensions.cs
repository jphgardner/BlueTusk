using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Subscriptions;
using BlueTusk.EntityFrameworkCore.Subscriptions.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

public static class BlueTuskSubscriptionMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskSubscriptionOperation> CreateBlueTuskSubscription(
        this MigrationBuilder migrationBuilder,
        BlueTuskSubscriptionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskSubscriptionMetadata.ValidateForCreate(definition);
        var operation = new CreateBlueTuskSubscriptionOperation { Definition = definition };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateBlueTuskSubscriptionOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskSubscriptionOperation> CreateBlueTuskSubscription(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateBlueTuskSubscription(migrationBuilder, BlueTuskSubscriptionMetadata.DeserializeDefinition(serializedDefinition));

    public static OperationBuilder<AlterBlueTuskSubscriptionOperation> AlterBlueTuskSubscription(
        this MigrationBuilder migrationBuilder,
        BlueTuskSubscriptionDefinition oldDefinition,
        BlueTuskSubscriptionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskSubscriptionMetadata.Validate(oldDefinition);
        BlueTuskSubscriptionMetadata.Validate(definition);
        var operation = new AlterBlueTuskSubscriptionOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskSubscriptionOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterBlueTuskSubscriptionOperation> AlterBlueTuskSubscription(
        this MigrationBuilder migrationBuilder,
        string serializedOldDefinition,
        string serializedDefinition) =>
        AlterBlueTuskSubscription(
            migrationBuilder,
            BlueTuskSubscriptionMetadata.DeserializeDefinition(serializedOldDefinition),
            BlueTuskSubscriptionMetadata.DeserializeDefinition(serializedDefinition));

    public static OperationBuilder<DropBlueTuskSubscriptionOperation> DropBlueTuskSubscription(
        this MigrationBuilder migrationBuilder,
        string name,
        bool hasSlot = true)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropBlueTuskSubscriptionOperation
        {
            Name = name,
            HasSlot = hasSlot,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskSubscriptionOperation>(operation);
    }

    public static OperationBuilder<RenameBlueTuskSubscriptionOperation> RenameBlueTuskSubscription(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new RenameBlueTuskSubscriptionOperation { Name = name, NewName = newName };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameBlueTuskSubscriptionOperation>(operation);
    }

    public static OperationBuilder<RefreshBlueTuskSubscriptionOperation> RefreshBlueTuskSubscription(
        this MigrationBuilder migrationBuilder,
        string name,
        bool copyData = true)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new RefreshBlueTuskSubscriptionOperation { Name = name, CopyData = copyData };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RefreshBlueTuskSubscriptionOperation>(operation);
    }

    public static OperationBuilder<RefreshBlueTuskSubscriptionSequencesOperation>
        RefreshBlueTuskSubscriptionSequences(this MigrationBuilder migrationBuilder, string name)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new RefreshBlueTuskSubscriptionSequencesOperation { Name = name };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RefreshBlueTuskSubscriptionSequencesOperation>(operation);
    }

    public static OperationBuilder<SkipBlueTuskSubscriptionTransactionOperation> SkipBlueTuskSubscriptionTransaction(
        this MigrationBuilder migrationBuilder,
        string name,
        string? finishLsn = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (finishLsn is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(finishLsn);
        }

        var operation = new SkipBlueTuskSubscriptionTransactionOperation
        {
            Name = name,
            FinishLsn = finishLsn,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<SkipBlueTuskSubscriptionTransactionOperation>(operation);
    }
}

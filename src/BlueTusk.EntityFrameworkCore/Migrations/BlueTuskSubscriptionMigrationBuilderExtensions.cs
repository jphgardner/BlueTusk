using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Subscriptions;
using BlueTusk.EntityFrameworkCore.Subscriptions.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

public static class BlueTuskSubscriptionMigrationBuilderExtensions
{
    public static OperationBuilder<CreateSubscriptionOperation> CreateSubscription(
        this MigrationBuilder migrationBuilder,
        BlueTuskSubscriptionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskSubscriptionMetadata.ValidateForCreate(definition);
        var operation = new CreateSubscriptionOperation { Definition = definition };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateSubscriptionOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateSubscriptionOperation> CreateSubscription(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateSubscription(migrationBuilder, BlueTuskSubscriptionMetadata.DeserializeDefinition(serializedDefinition));

    public static OperationBuilder<AlterSubscriptionOperation> AlterSubscription(
        this MigrationBuilder migrationBuilder,
        BlueTuskSubscriptionDefinition oldDefinition,
        BlueTuskSubscriptionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        BlueTuskSubscriptionMetadata.Validate(oldDefinition);
        BlueTuskSubscriptionMetadata.Validate(definition);
        var operation = new AlterSubscriptionOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterSubscriptionOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterSubscriptionOperation> AlterSubscription(
        this MigrationBuilder migrationBuilder,
        string serializedOldDefinition,
        string serializedDefinition) =>
        AlterSubscription(
            migrationBuilder,
            BlueTuskSubscriptionMetadata.DeserializeDefinition(serializedOldDefinition),
            BlueTuskSubscriptionMetadata.DeserializeDefinition(serializedDefinition));

    public static OperationBuilder<DropSubscriptionOperation> DropSubscription(
        this MigrationBuilder migrationBuilder,
        string name,
        bool hasSlot = true)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropSubscriptionOperation
        {
            Name = name,
            HasSlot = hasSlot,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropSubscriptionOperation>(operation);
    }

    public static OperationBuilder<RenameSubscriptionOperation> RenameSubscription(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new RenameSubscriptionOperation { Name = name, NewName = newName };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameSubscriptionOperation>(operation);
    }

    public static OperationBuilder<RefreshSubscriptionOperation> RefreshSubscription(
        this MigrationBuilder migrationBuilder,
        string name,
        bool copyData = true)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new RefreshSubscriptionOperation { Name = name, CopyData = copyData };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RefreshSubscriptionOperation>(operation);
    }

    public static OperationBuilder<RefreshSubscriptionSequencesOperation>
        RefreshSubscriptionSequences(this MigrationBuilder migrationBuilder, string name)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new RefreshSubscriptionSequencesOperation { Name = name };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RefreshSubscriptionSequencesOperation>(operation);
    }

    public static OperationBuilder<SkipSubscriptionTransactionOperation> SkipSubscriptionTransaction(
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

        var operation = new SkipSubscriptionTransactionOperation
        {
            Name = name,
            FinishLsn = finishLsn,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<SkipSubscriptionTransactionOperation>(operation);
    }
}

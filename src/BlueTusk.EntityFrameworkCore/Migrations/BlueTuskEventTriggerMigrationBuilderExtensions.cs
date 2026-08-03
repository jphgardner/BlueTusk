using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.EventTriggers;
using BlueTusk.EntityFrameworkCore.EventTriggers.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for PostgreSQL database-level event triggers.</summary>
public static class BlueTuskEventTriggerMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskEventTriggerOperation> CreateBlueTuskEventTrigger(
        this MigrationBuilder builder,
        BlueTuskEventTriggerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(builder);
        BlueTuskEventTriggerMetadata.Validate(definition);
        var operation = new CreateBlueTuskEventTriggerOperation
        {
            Definition = BlueTuskEventTriggerMetadata.Normalize(definition),
        };
        builder.Operations.Add(operation);
        return new OperationBuilder<CreateBlueTuskEventTriggerOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskEventTriggerOperation> CreateBlueTuskEventTrigger(
        this MigrationBuilder builder,
        string serializedDefinition) =>
        CreateBlueTuskEventTrigger(builder, BlueTuskEventTriggerMetadata.DeserializeDefinition(serializedDefinition));

    public static OperationBuilder<DropBlueTuskEventTriggerOperation> DropBlueTuskEventTrigger(
        this MigrationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropBlueTuskEventTriggerOperation
        {
            Name = name,
            IsDestructiveChange = true,
        };
        builder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskEventTriggerOperation>(operation);
    }

    public static OperationBuilder<RenameBlueTuskEventTriggerOperation> RenameBlueTuskEventTrigger(
        this MigrationBuilder builder,
        string name,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new RenameBlueTuskEventTriggerOperation { Name = name, NewName = newName };
        builder.Operations.Add(operation);
        return new OperationBuilder<RenameBlueTuskEventTriggerOperation>(operation);
    }

    public static OperationBuilder<AlterBlueTuskEventTriggerEnabledModeOperation>
        AlterBlueTuskEventTriggerEnabledMode(
            this MigrationBuilder builder,
            string name,
            BlueTuskEventTriggerEnabledMode enabledMode)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(enabledMode))
        {
            throw new ArgumentOutOfRangeException(nameof(enabledMode));
        }

        var operation = new AlterBlueTuskEventTriggerEnabledModeOperation
        {
            Name = name,
            EnabledMode = enabledMode,
        };
        builder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskEventTriggerEnabledModeOperation>(operation);
    }
}

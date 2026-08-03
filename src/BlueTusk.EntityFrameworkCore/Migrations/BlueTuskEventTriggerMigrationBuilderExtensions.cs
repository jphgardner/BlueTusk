using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.EventTriggers;
using BlueTusk.EntityFrameworkCore.EventTriggers.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for PostgreSQL database-level event triggers.</summary>
public static class BlueTuskEventTriggerMigrationBuilderExtensions
{
    public static OperationBuilder<CreateEventTriggerOperation> CreateEventTrigger(
        this MigrationBuilder builder,
        BlueTuskEventTriggerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(builder);
        BlueTuskEventTriggerMetadata.Validate(definition);
        var operation = new CreateEventTriggerOperation
        {
            Definition = BlueTuskEventTriggerMetadata.Normalize(definition),
        };
        builder.Operations.Add(operation);
        return new OperationBuilder<CreateEventTriggerOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateEventTriggerOperation> CreateEventTrigger(
        this MigrationBuilder builder,
        string serializedDefinition) =>
        CreateEventTrigger(builder, BlueTuskEventTriggerMetadata.DeserializeDefinition(serializedDefinition));

    public static OperationBuilder<DropEventTriggerOperation> DropEventTrigger(
        this MigrationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropEventTriggerOperation
        {
            Name = name,
            IsDestructiveChange = true,
        };
        builder.Operations.Add(operation);
        return new OperationBuilder<DropEventTriggerOperation>(operation);
    }

    public static OperationBuilder<RenameEventTriggerOperation> RenameEventTrigger(
        this MigrationBuilder builder,
        string name,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new RenameEventTriggerOperation { Name = name, NewName = newName };
        builder.Operations.Add(operation);
        return new OperationBuilder<RenameEventTriggerOperation>(operation);
    }

    public static OperationBuilder<AlterEventTriggerEnabledModeOperation>
        AlterEventTriggerEnabledMode(
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

        var operation = new AlterEventTriggerEnabledModeOperation
        {
            Name = name,
            EnabledMode = enabledMode,
        };
        builder.Operations.Add(operation);
        return new OperationBuilder<AlterEventTriggerEnabledModeOperation>(operation);
    }
}

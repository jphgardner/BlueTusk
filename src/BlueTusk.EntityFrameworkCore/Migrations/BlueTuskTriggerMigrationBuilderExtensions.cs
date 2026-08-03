using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Triggers;
using BlueTusk.EntityFrameworkCore.Triggers.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

public static class BlueTuskTriggerMigrationBuilderExtensions
{
    public static OperationBuilder<CreateTriggerOperation> CreateTrigger(
        this MigrationBuilder migrationBuilder,
        string table,
        BlueTuskTriggerDefinition definition,
        string? schema = null,
        bool orReplace = false)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        BlueTuskTriggerMetadata.Validate(definition);
        if (orReplace && definition.IsConstraint)
        {
            throw new ArgumentException("PostgreSQL cannot replace a constraint trigger in place.", nameof(orReplace));
        }

        var operation = new CreateTriggerOperation
        {
            Table = table,
            Schema = schema,
            Definition = definition,
            OrReplace = orReplace,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateTriggerOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateTriggerOperation> CreateTrigger(
        this MigrationBuilder migrationBuilder,
        string table,
        string serializedDefinition,
        string? schema = null,
        bool orReplace = false) =>
        CreateTrigger(
            migrationBuilder,
            table,
            BlueTuskTriggerMetadata.DeserializeDefinition(serializedDefinition),
            schema,
            orReplace);

    public static OperationBuilder<DropTriggerOperation> DropTrigger(
        this MigrationBuilder migrationBuilder,
        string table,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropTriggerOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropTriggerOperation>(operation);
    }

    public static OperationBuilder<RenameTriggerOperation> RenameTrigger(
        this MigrationBuilder migrationBuilder,
        string table,
        string name,
        string newName,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var operation = new RenameTriggerOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            NewName = newName,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameTriggerOperation>(operation);
    }

    public static OperationBuilder<AlterTriggerEnabledModeOperation> AlterTriggerEnabledMode(
        this MigrationBuilder migrationBuilder,
        string table,
        string name,
        BlueTuskTriggerEnabledMode enabledMode,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(enabledMode))
        {
            throw new ArgumentOutOfRangeException(nameof(enabledMode));
        }

        var operation = new AlterTriggerEnabledModeOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            EnabledMode = enabledMode,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterTriggerEnabledModeOperation>(operation);
    }
}

using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Triggers;
using BlueTusk.EntityFrameworkCore.Triggers.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

public static class BlueTuskTriggerMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskTriggerOperation> CreateBlueTuskTrigger(
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

        var operation = new CreateBlueTuskTriggerOperation
        {
            Table = table,
            Schema = schema,
            Definition = definition,
            OrReplace = orReplace,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateBlueTuskTriggerOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskTriggerOperation> CreateBlueTuskTrigger(
        this MigrationBuilder migrationBuilder,
        string table,
        string serializedDefinition,
        string? schema = null,
        bool orReplace = false) =>
        CreateBlueTuskTrigger(
            migrationBuilder,
            table,
            BlueTuskTriggerMetadata.DeserializeDefinition(serializedDefinition),
            schema,
            orReplace);

    public static OperationBuilder<DropBlueTuskTriggerOperation> DropBlueTuskTrigger(
        this MigrationBuilder migrationBuilder,
        string table,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropBlueTuskTriggerOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskTriggerOperation>(operation);
    }

    public static OperationBuilder<RenameBlueTuskTriggerOperation> RenameBlueTuskTrigger(
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
        var operation = new RenameBlueTuskTriggerOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            NewName = newName,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameBlueTuskTriggerOperation>(operation);
    }

    public static OperationBuilder<AlterBlueTuskTriggerEnabledModeOperation> AlterBlueTuskTriggerEnabledMode(
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

        var operation = new AlterBlueTuskTriggerEnabledModeOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            EnabledMode = enabledMode,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskTriggerEnabledModeOperation>(operation);
    }
}

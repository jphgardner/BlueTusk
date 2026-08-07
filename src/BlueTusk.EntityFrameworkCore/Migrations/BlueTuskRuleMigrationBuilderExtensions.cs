using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Rules;
using BlueTusk.EntityFrameworkCore.Rules.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

public static class BlueTuskRuleMigrationBuilderExtensions
{
    public static OperationBuilder<CreateRuleOperation> CreateRule(
        this MigrationBuilder migrationBuilder,
        string table,
        BlueTuskRuleDefinition definition,
        string? schema = null,
        bool orReplace = false)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        BlueTuskRuleMetadata.Validate(definition);
        var operation = new CreateRuleOperation
        {
            Table = table,
            Schema = schema,
            Definition = definition,
            OrReplace = orReplace,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateRuleOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateRuleOperation> CreateRule(
        this MigrationBuilder migrationBuilder,
        string table,
        string serializedDefinition,
        string? schema = null,
        bool orReplace = false) =>
        CreateRule(
            migrationBuilder,
            table,
            BlueTuskRuleMetadata.DeserializeDefinition(serializedDefinition),
            schema,
            orReplace);

    public static OperationBuilder<DropRuleOperation> DropRule(
        this MigrationBuilder migrationBuilder,
        string table,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropRuleOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropRuleOperation>(operation);
    }

    public static OperationBuilder<RenameRuleOperation> RenameRule(
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
        var operation = new RenameRuleOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            NewName = newName,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameRuleOperation>(operation);
    }

    public static OperationBuilder<AlterRuleEnabledModeOperation> AlterRuleEnabledMode(
        this MigrationBuilder migrationBuilder,
        string table,
        string name,
        BlueTuskRuleEnabledMode enabledMode,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(enabledMode))
        {
            throw new ArgumentOutOfRangeException(nameof(enabledMode));
        }

        var operation = new AlterRuleEnabledModeOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            EnabledMode = enabledMode,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterRuleEnabledModeOperation>(operation);
    }
}

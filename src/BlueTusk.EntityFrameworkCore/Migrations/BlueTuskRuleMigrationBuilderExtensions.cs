using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Rules;
using BlueTusk.EntityFrameworkCore.Rules.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

public static class BlueTuskRuleMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskRuleOperation> CreateBlueTuskRule(
        this MigrationBuilder migrationBuilder,
        string table,
        BlueTuskRuleDefinition definition,
        string? schema = null,
        bool orReplace = false)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        BlueTuskRuleMetadata.Validate(definition);
        var operation = new CreateBlueTuskRuleOperation
        {
            Table = table,
            Schema = schema,
            Definition = definition,
            OrReplace = orReplace,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateBlueTuskRuleOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskRuleOperation> CreateBlueTuskRule(
        this MigrationBuilder migrationBuilder,
        string table,
        string serializedDefinition,
        string? schema = null,
        bool orReplace = false) =>
        CreateBlueTuskRule(
            migrationBuilder,
            table,
            BlueTuskRuleMetadata.DeserializeDefinition(serializedDefinition),
            schema,
            orReplace);

    public static OperationBuilder<DropBlueTuskRuleOperation> DropBlueTuskRule(
        this MigrationBuilder migrationBuilder,
        string table,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropBlueTuskRuleOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            IsDestructiveChange = true,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskRuleOperation>(operation);
    }

    public static OperationBuilder<RenameBlueTuskRuleOperation> RenameBlueTuskRule(
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
        var operation = new RenameBlueTuskRuleOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            NewName = newName,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameBlueTuskRuleOperation>(operation);
    }

    public static OperationBuilder<AlterBlueTuskRuleEnabledModeOperation> AlterBlueTuskRuleEnabledMode(
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

        var operation = new AlterBlueTuskRuleEnabledModeOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            EnabledMode = enabledMode,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskRuleEnabledModeOperation>(operation);
    }
}

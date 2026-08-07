using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for PostgreSQL row-level security.</summary>
public static class BlueTuskRowLevelSecurityMigrationBuilderExtensions
{
    public static OperationBuilder<CreateRowSecurityPolicyOperation> CreateRowSecurityPolicy(
        this MigrationBuilder migrationBuilder,
        string table,
        BlueTuskRowSecurityPolicyDefinition definition,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(definition);
        BlueTuskRowLevelSecurityBuilder.ValidatePolicy(definition);
        var operation = new CreateRowSecurityPolicyOperation
        {
            Table = table,
            Schema = schema,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateRowSecurityPolicyOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateRowSecurityPolicyOperation> CreateRowSecurityPolicy(
        this MigrationBuilder migrationBuilder,
        string table,
        string serializedDefinition,
        string? schema = null) =>
        CreateRowSecurityPolicy(
            migrationBuilder,
            table,
            BlueTuskRowLevelSecurityMetadata.DeserializePolicy(serializedDefinition),
            schema);

    public static OperationBuilder<DropRowSecurityPolicyOperation> DropRowSecurityPolicy(
        this MigrationBuilder migrationBuilder,
        string table,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropRowSecurityPolicyOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropRowSecurityPolicyOperation>(operation);
    }

    public static OperationBuilder<AlterRowSecurityPolicyOperation> AlterRowSecurityPolicy(
        this MigrationBuilder migrationBuilder,
        string table,
        BlueTuskRowSecurityPolicyDefinition definition,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(definition);
        BlueTuskRowLevelSecurityBuilder.ValidatePolicy(definition);
        var operation = new AlterRowSecurityPolicyOperation
        {
            Table = table,
            Schema = schema,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterRowSecurityPolicyOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterRowSecurityPolicyOperation> AlterRowSecurityPolicy(
        this MigrationBuilder migrationBuilder,
        string table,
        string serializedDefinition,
        string? schema = null) =>
        AlterRowSecurityPolicy(
            migrationBuilder,
            table,
            BlueTuskRowLevelSecurityMetadata.DeserializePolicy(serializedDefinition),
            schema);

    public static OperationBuilder<RenameRowSecurityPolicyOperation> RenameRowSecurityPolicy(
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
        var operation = new RenameRowSecurityPolicyOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            NewName = newName,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameRowSecurityPolicyOperation>(operation);
    }

    public static OperationBuilder<AlterRowLevelSecurityOperation> AlterRowLevelSecurity(
        this MigrationBuilder migrationBuilder,
        string table,
        bool? enabled = null,
        bool? forced = null,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (enabled is null && forced is null)
        {
            throw new ArgumentException("At least one row-level security setting must be supplied.");
        }

        var operation = new AlterRowLevelSecurityOperation
        {
            Table = table,
            Schema = schema,
            Enabled = enabled,
            Forced = forced,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterRowLevelSecurityOperation>(operation);
    }
}

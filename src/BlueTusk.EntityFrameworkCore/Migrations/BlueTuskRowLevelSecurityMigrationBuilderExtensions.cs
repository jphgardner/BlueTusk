using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for PostgreSQL row-level security.</summary>
public static class BlueTuskRowLevelSecurityMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskRowSecurityPolicyOperation> CreateBlueTuskRowSecurityPolicy(
        this MigrationBuilder migrationBuilder,
        string table,
        BlueTuskRowSecurityPolicyDefinition definition,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(definition);
        BlueTuskRowLevelSecurityBuilder.ValidatePolicy(definition);
        var operation = new CreateBlueTuskRowSecurityPolicyOperation
        {
            Table = table,
            Schema = schema,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<CreateBlueTuskRowSecurityPolicyOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskRowSecurityPolicyOperation> CreateBlueTuskRowSecurityPolicy(
        this MigrationBuilder migrationBuilder,
        string table,
        string serializedDefinition,
        string? schema = null) =>
        CreateBlueTuskRowSecurityPolicy(
            migrationBuilder,
            table,
            BlueTuskRowLevelSecurityMetadata.DeserializePolicy(serializedDefinition),
            schema);

    public static OperationBuilder<DropBlueTuskRowSecurityPolicyOperation> DropBlueTuskRowSecurityPolicy(
        this MigrationBuilder migrationBuilder,
        string table,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = new DropBlueTuskRowSecurityPolicyOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<DropBlueTuskRowSecurityPolicyOperation>(operation);
    }

    public static OperationBuilder<AlterBlueTuskRowSecurityPolicyOperation> AlterBlueTuskRowSecurityPolicy(
        this MigrationBuilder migrationBuilder,
        string table,
        BlueTuskRowSecurityPolicyDefinition definition,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(definition);
        BlueTuskRowLevelSecurityBuilder.ValidatePolicy(definition);
        var operation = new AlterBlueTuskRowSecurityPolicyOperation
        {
            Table = table,
            Schema = schema,
            Definition = definition,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskRowSecurityPolicyOperation>(operation);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterBlueTuskRowSecurityPolicyOperation> AlterBlueTuskRowSecurityPolicy(
        this MigrationBuilder migrationBuilder,
        string table,
        string serializedDefinition,
        string? schema = null) =>
        AlterBlueTuskRowSecurityPolicy(
            migrationBuilder,
            table,
            BlueTuskRowLevelSecurityMetadata.DeserializePolicy(serializedDefinition),
            schema);

    public static OperationBuilder<RenameBlueTuskRowSecurityPolicyOperation> RenameBlueTuskRowSecurityPolicy(
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
        var operation = new RenameBlueTuskRowSecurityPolicyOperation
        {
            Table = table,
            Schema = schema,
            Name = name,
            NewName = newName,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<RenameBlueTuskRowSecurityPolicyOperation>(operation);
    }

    public static OperationBuilder<AlterBlueTuskRowLevelSecurityOperation> AlterBlueTuskRowLevelSecurity(
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

        var operation = new AlterBlueTuskRowLevelSecurityOperation
        {
            Table = table,
            Schema = schema,
            Enabled = enabled,
            Forced = forced,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AlterBlueTuskRowLevelSecurityOperation>(operation);
    }
}

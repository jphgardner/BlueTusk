using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Views;
using BlueTusk.EntityFrameworkCore.Views.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration-builder extensions for PostgreSQL views and materialized views.</summary>
public static class BlueTuskViewMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskViewOperation> CreateBlueTuskView(
        this MigrationBuilder migrationBuilder,
        BlueTuskViewDefinition definition)
    {
        BlueTuskViewMetadata.Validate(definition);
        return Add(migrationBuilder, new CreateBlueTuskViewOperation
        {
            Definition = BlueTuskViewMetadata.Normalize(definition),
        });
    }

    public static OperationBuilder<ReplaceBlueTuskViewOperation> ReplaceBlueTuskView(
        this MigrationBuilder migrationBuilder,
        BlueTuskViewDefinition definition,
        BlueTuskViewDefinition oldDefinition)
    {
        BlueTuskViewAlterationPlanner.ValidateReplacement(oldDefinition, definition);
        return Add(migrationBuilder, new ReplaceBlueTuskViewOperation
        {
            Definition = BlueTuskViewMetadata.Normalize(definition),
            OldDefinition = BlueTuskViewMetadata.Normalize(oldDefinition),
        });
    }

    public static OperationBuilder<CreateBlueTuskMaterializedViewOperation> CreateBlueTuskMaterializedView(
        this MigrationBuilder migrationBuilder,
        BlueTuskMaterializedViewDefinition definition)
    {
        BlueTuskViewMetadata.Validate(definition);
        return Add(migrationBuilder, new CreateBlueTuskMaterializedViewOperation
        {
            Definition = BlueTuskViewMetadata.Normalize(definition),
        });
    }

    public static OperationBuilder<AlterBlueTuskMaterializedViewOperation> AlterBlueTuskMaterializedView(
        this MigrationBuilder migrationBuilder,
        BlueTuskMaterializedViewDefinition definition,
        BlueTuskMaterializedViewDefinition oldDefinition)
    {
        BlueTuskViewAlterationPlanner.ValidateMaterializedAlteration(oldDefinition, definition);
        return Add(migrationBuilder, new AlterBlueTuskMaterializedViewOperation
        {
            Definition = BlueTuskViewMetadata.Normalize(definition),
            OldDefinition = BlueTuskViewMetadata.Normalize(oldDefinition),
            IsDestructiveChange = oldDefinition.IsPopulated && !definition.IsPopulated,
        });
    }

    public static OperationBuilder<DropBlueTuskViewOperation> DropBlueTuskView(
        this MigrationBuilder migrationBuilder,
        BlueTuskViewKind kind,
        string name,
        string? schema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Add(migrationBuilder, new DropBlueTuskViewOperation
        {
            Kind = kind,
            Name = name,
            Schema = schema,
            IsDestructiveChange = true,
        });
    }

    public static OperationBuilder<RenameBlueTuskViewOperation> RenameBlueTuskView(
        this MigrationBuilder migrationBuilder,
        BlueTuskViewKind kind,
        string name,
        string newName,
        string? schema = null,
        string? newSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        return Add(migrationBuilder, new RenameBlueTuskViewOperation
        {
            Kind = kind,
            Name = name,
            Schema = schema,
            NewName = newName,
            NewSchema = newSchema ?? schema,
        });
    }

    public static OperationBuilder<RefreshBlueTuskMaterializedViewOperation> RefreshBlueTuskMaterializedView(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null,
        bool concurrently = false,
        bool withData = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (concurrently && !withData)
        {
            throw new ArgumentException(
                "PostgreSQL cannot refresh a materialized view CONCURRENTLY WITH NO DATA.",
                nameof(withData));
        }

        return Add(migrationBuilder, new RefreshBlueTuskMaterializedViewOperation
        {
            Name = name,
            Schema = schema,
            Concurrently = concurrently,
            WithData = withData,
            IsDestructiveChange = !withData,
        });
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskViewOperation> CreateBlueTuskView(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateBlueTuskView(migrationBuilder, BlueTuskViewMetadata.DeserializeView(serializedDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<ReplaceBlueTuskViewOperation> ReplaceBlueTuskView(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        ReplaceBlueTuskView(
            migrationBuilder,
            BlueTuskViewMetadata.DeserializeView(serializedDefinition),
            BlueTuskViewMetadata.DeserializeView(serializedOldDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskMaterializedViewOperation> CreateBlueTuskMaterializedView(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateBlueTuskMaterializedView(
            migrationBuilder,
            BlueTuskViewMetadata.DeserializeMaterializedView(serializedDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterBlueTuskMaterializedViewOperation> AlterBlueTuskMaterializedView(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        AlterBlueTuskMaterializedView(
            migrationBuilder,
            BlueTuskViewMetadata.DeserializeMaterializedView(serializedDefinition),
            BlueTuskViewMetadata.DeserializeMaterializedView(serializedOldDefinition));

    private static OperationBuilder<TOperation> Add<TOperation>(
        MigrationBuilder migrationBuilder,
        TOperation operation)
        where TOperation : MigrationOperation
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentNullException.ThrowIfNull(operation);
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<TOperation>(operation);
    }
}

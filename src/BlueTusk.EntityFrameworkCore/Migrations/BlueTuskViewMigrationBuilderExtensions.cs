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
    public static OperationBuilder<CreateViewOperation> CreateView(
        this MigrationBuilder migrationBuilder,
        BlueTuskViewDefinition definition)
    {
        BlueTuskViewMetadata.Validate(definition);
        return Add(migrationBuilder, new CreateViewOperation
        {
            Definition = BlueTuskViewMetadata.Normalize(definition),
        });
    }

    public static OperationBuilder<ReplaceViewOperation> ReplaceView(
        this MigrationBuilder migrationBuilder,
        BlueTuskViewDefinition definition,
        BlueTuskViewDefinition oldDefinition)
    {
        BlueTuskViewAlterationPlanner.ValidateReplacement(oldDefinition, definition);
        return Add(migrationBuilder, new ReplaceViewOperation
        {
            Definition = BlueTuskViewMetadata.Normalize(definition),
            OldDefinition = BlueTuskViewMetadata.Normalize(oldDefinition),
        });
    }

    public static OperationBuilder<CreateMaterializedViewOperation> CreateMaterializedView(
        this MigrationBuilder migrationBuilder,
        BlueTuskMaterializedViewDefinition definition)
    {
        BlueTuskViewMetadata.Validate(definition);
        return Add(migrationBuilder, new CreateMaterializedViewOperation
        {
            Definition = BlueTuskViewMetadata.Normalize(definition),
        });
    }

    public static OperationBuilder<AlterMaterializedViewOperation> AlterMaterializedView(
        this MigrationBuilder migrationBuilder,
        BlueTuskMaterializedViewDefinition definition,
        BlueTuskMaterializedViewDefinition oldDefinition)
    {
        BlueTuskViewAlterationPlanner.ValidateMaterializedAlteration(oldDefinition, definition);
        return Add(migrationBuilder, new AlterMaterializedViewOperation
        {
            Definition = BlueTuskViewMetadata.Normalize(definition),
            OldDefinition = BlueTuskViewMetadata.Normalize(oldDefinition),
            IsDestructiveChange = oldDefinition.IsPopulated && !definition.IsPopulated,
        });
    }

    public static OperationBuilder<DropViewOperation> DropView(
        this MigrationBuilder migrationBuilder,
        BlueTuskViewKind kind,
        string name,
        string? schema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Add(migrationBuilder, new DropViewOperation
        {
            Kind = kind,
            Name = name,
            Schema = schema,
            IsDestructiveChange = true,
        });
    }

    public static OperationBuilder<RenameViewOperation> RenameView(
        this MigrationBuilder migrationBuilder,
        BlueTuskViewKind kind,
        string name,
        string newName,
        string? schema = null,
        string? newSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        return Add(migrationBuilder, new RenameViewOperation
        {
            Kind = kind,
            Name = name,
            Schema = schema,
            NewName = newName,
            NewSchema = newSchema ?? schema,
        });
    }

    public static OperationBuilder<RefreshMaterializedViewOperation> RefreshMaterializedView(
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

        return Add(migrationBuilder, new RefreshMaterializedViewOperation
        {
            Name = name,
            Schema = schema,
            Concurrently = concurrently,
            WithData = withData,
            IsDestructiveChange = !withData,
        });
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateViewOperation> CreateView(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateView(migrationBuilder, BlueTuskViewMetadata.DeserializeView(serializedDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<ReplaceViewOperation> ReplaceView(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        ReplaceView(
            migrationBuilder,
            BlueTuskViewMetadata.DeserializeView(serializedDefinition),
            BlueTuskViewMetadata.DeserializeView(serializedOldDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateMaterializedViewOperation> CreateMaterializedView(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateMaterializedView(
            migrationBuilder,
            BlueTuskViewMetadata.DeserializeMaterializedView(serializedDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterMaterializedViewOperation> AlterMaterializedView(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        AlterMaterializedView(
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

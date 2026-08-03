using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Routines;
using BlueTusk.EntityFrameworkCore.Routines.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Signature-aware migration-builder extensions for PostgreSQL routines.</summary>
public static class BlueTuskRoutineMigrationBuilderExtensions
{
    public static OperationBuilder<CreateRoutineOperation> CreateRoutine(
        this MigrationBuilder migrationBuilder,
        BlueTuskRoutineDefinition definition)
    {
        BlueTuskRoutineMetadata.Validate(definition);
        return Add(migrationBuilder, new CreateRoutineOperation
        {
            Definition = BlueTuskRoutineMetadata.Normalize(definition),
        });
    }

    public static OperationBuilder<ReplaceRoutineOperation> ReplaceRoutine(
        this MigrationBuilder migrationBuilder,
        BlueTuskRoutineDefinition definition,
        BlueTuskRoutineDefinition oldDefinition)
    {
        BlueTuskRoutineAlterationPlanner.ValidateReplacement(oldDefinition, definition);
        return Add(migrationBuilder, new ReplaceRoutineOperation
        {
            Definition = BlueTuskRoutineMetadata.Normalize(definition),
            OldDefinition = BlueTuskRoutineMetadata.Normalize(oldDefinition),
        });
    }

    public static OperationBuilder<DropRoutineOperation> DropRoutine(
        this MigrationBuilder migrationBuilder,
        BlueTuskRoutineKind kind,
        string name,
        string identityArgumentsSql = "",
        string? schema = null) =>
        Add(migrationBuilder, new DropRoutineOperation
        {
            Kind = kind,
            Name = name,
            Schema = schema,
            IdentityArgumentsSql = identityArgumentsSql,
            IsDestructiveChange = true,
        });

    public static OperationBuilder<RenameRoutineOperation> RenameRoutine(
        this MigrationBuilder migrationBuilder,
        BlueTuskRoutineKind kind,
        string name,
        string identityArgumentsSql,
        string newName,
        string? schema = null,
        string? newSchema = null) =>
        Add(migrationBuilder, new RenameRoutineOperation
        {
            Kind = kind,
            Name = name,
            Schema = schema,
            IdentityArgumentsSql = identityArgumentsSql,
            NewName = newName,
            NewSchema = newSchema,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateRoutineOperation> CreateRoutine(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateRoutine(migrationBuilder, BlueTuskRoutineMetadata.DeserializeDefinition(serializedDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<ReplaceRoutineOperation> ReplaceRoutine(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        ReplaceRoutine(
            migrationBuilder,
            BlueTuskRoutineMetadata.DeserializeDefinition(serializedDefinition),
            BlueTuskRoutineMetadata.DeserializeDefinition(serializedOldDefinition));

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

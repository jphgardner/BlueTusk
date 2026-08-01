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
    public static OperationBuilder<CreateBlueTuskRoutineOperation> CreateBlueTuskRoutine(
        this MigrationBuilder migrationBuilder,
        BlueTuskRoutineDefinition definition)
    {
        BlueTuskRoutineMetadata.Validate(definition);
        return Add(migrationBuilder, new CreateBlueTuskRoutineOperation
        {
            Definition = BlueTuskRoutineMetadata.Normalize(definition),
        });
    }

    public static OperationBuilder<ReplaceBlueTuskRoutineOperation> ReplaceBlueTuskRoutine(
        this MigrationBuilder migrationBuilder,
        BlueTuskRoutineDefinition definition,
        BlueTuskRoutineDefinition oldDefinition)
    {
        BlueTuskRoutineAlterationPlanner.ValidateReplacement(oldDefinition, definition);
        return Add(migrationBuilder, new ReplaceBlueTuskRoutineOperation
        {
            Definition = BlueTuskRoutineMetadata.Normalize(definition),
            OldDefinition = BlueTuskRoutineMetadata.Normalize(oldDefinition),
        });
    }

    public static OperationBuilder<DropBlueTuskRoutineOperation> DropBlueTuskRoutine(
        this MigrationBuilder migrationBuilder,
        BlueTuskRoutineKind kind,
        string name,
        string identityArgumentsSql = "",
        string? schema = null) =>
        Add(migrationBuilder, new DropBlueTuskRoutineOperation
        {
            Kind = kind,
            Name = name,
            Schema = schema,
            IdentityArgumentsSql = identityArgumentsSql,
            IsDestructiveChange = true,
        });

    public static OperationBuilder<RenameBlueTuskRoutineOperation> RenameBlueTuskRoutine(
        this MigrationBuilder migrationBuilder,
        BlueTuskRoutineKind kind,
        string name,
        string identityArgumentsSql,
        string newName,
        string? schema = null,
        string? newSchema = null) =>
        Add(migrationBuilder, new RenameBlueTuskRoutineOperation
        {
            Kind = kind,
            Name = name,
            Schema = schema,
            IdentityArgumentsSql = identityArgumentsSql,
            NewName = newName,
            NewSchema = newSchema,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskRoutineOperation> CreateBlueTuskRoutine(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateBlueTuskRoutine(migrationBuilder, BlueTuskRoutineMetadata.DeserializeDefinition(serializedDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<ReplaceBlueTuskRoutineOperation> ReplaceBlueTuskRoutine(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        ReplaceBlueTuskRoutine(
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

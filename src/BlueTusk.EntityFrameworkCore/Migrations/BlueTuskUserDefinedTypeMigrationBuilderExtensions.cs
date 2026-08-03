using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration-builder extensions for PostgreSQL enum, domain, composite, and range types.</summary>
public static class BlueTuskUserDefinedTypeMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskEnumTypeOperation> CreateBlueTuskEnumType(
        this MigrationBuilder migrationBuilder,
        BlueTuskEnumTypeDefinition definition) =>
        Add(migrationBuilder, new CreateBlueTuskEnumTypeOperation { Definition = definition });

    public static OperationBuilder<AlterBlueTuskEnumTypeOperation> AlterBlueTuskEnumType(
        this MigrationBuilder migrationBuilder,
        BlueTuskEnumTypeDefinition definition,
        BlueTuskEnumTypeDefinition oldDefinition) =>
        Add(migrationBuilder, new AlterBlueTuskEnumTypeOperation
        {
            Definition = definition,
            OldDefinition = oldDefinition,
        });

    public static OperationBuilder<DropBlueTuskEnumTypeOperation> DropBlueTuskEnumType(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null) =>
        Add(migrationBuilder, new DropBlueTuskEnumTypeOperation
        {
            Name = name,
            Schema = schema,
            IsDestructiveChange = true,
        });

    public static OperationBuilder<CreateBlueTuskDomainTypeOperation> CreateBlueTuskDomainType(
        this MigrationBuilder migrationBuilder,
        BlueTuskDomainTypeDefinition definition) =>
        Add(migrationBuilder, new CreateBlueTuskDomainTypeOperation { Definition = definition });

    public static OperationBuilder<AlterBlueTuskDomainTypeOperation> AlterBlueTuskDomainType(
        this MigrationBuilder migrationBuilder,
        BlueTuskDomainTypeDefinition definition,
        BlueTuskDomainTypeDefinition oldDefinition) =>
        Add(migrationBuilder, new AlterBlueTuskDomainTypeOperation
        {
            Definition = definition,
            OldDefinition = oldDefinition,
        });

    public static OperationBuilder<DropBlueTuskDomainTypeOperation> DropBlueTuskDomainType(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null) =>
        Add(migrationBuilder, new DropBlueTuskDomainTypeOperation
        {
            Name = name,
            Schema = schema,
            IsDestructiveChange = true,
        });

    public static OperationBuilder<CreateBlueTuskCompositeTypeOperation> CreateBlueTuskCompositeType(
        this MigrationBuilder migrationBuilder,
        BlueTuskCompositeTypeDefinition definition) =>
        Add(migrationBuilder, new CreateBlueTuskCompositeTypeOperation { Definition = definition });

    public static OperationBuilder<AlterBlueTuskCompositeTypeOperation> AlterBlueTuskCompositeType(
        this MigrationBuilder migrationBuilder,
        BlueTuskCompositeTypeDefinition definition,
        BlueTuskCompositeTypeDefinition oldDefinition) =>
        Add(migrationBuilder, new AlterBlueTuskCompositeTypeOperation
        {
            Definition = definition,
            OldDefinition = oldDefinition,
            IsDestructiveChange = BlueTuskUserDefinedTypeAlterationPlanner
                .PlanComposite(oldDefinition, definition)
                .Any(change => change.Kind == CompositeAttributeChangeKind.Drop),
        });

    public static OperationBuilder<DropBlueTuskCompositeTypeOperation> DropBlueTuskCompositeType(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null) =>
        Add(migrationBuilder, new DropBlueTuskCompositeTypeOperation
        {
            Name = name,
            Schema = schema,
            IsDestructiveChange = true,
        });

    public static OperationBuilder<CreateBlueTuskRangeTypeOperation> CreateBlueTuskRangeType(
        this MigrationBuilder migrationBuilder,
        BlueTuskRangeTypeDefinition definition) =>
        Add(migrationBuilder, new CreateBlueTuskRangeTypeOperation { Definition = definition });

    public static OperationBuilder<DropBlueTuskRangeTypeOperation> DropBlueTuskRangeType(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null) =>
        Add(migrationBuilder, new DropBlueTuskRangeTypeOperation
        {
            Name = name,
            Schema = schema,
            IsDestructiveChange = true,
        });

    public static OperationBuilder<RenameBlueTuskRangeTypeOperation> RenameBlueTuskRangeType(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName,
        string multirangeName,
        string newMultirangeName,
        string? schema = null,
        string? newSchema = null,
        string? multirangeSchema = null,
        string? newMultirangeSchema = null) =>
        Add(migrationBuilder, new RenameBlueTuskRangeTypeOperation
        {
            Name = name,
            Schema = schema,
            NewName = newName,
            NewSchema = newSchema,
            MultirangeName = multirangeName,
            MultirangeSchema = multirangeSchema,
            NewMultirangeName = newMultirangeName,
            NewMultirangeSchema = newMultirangeSchema,
        });

    public static OperationBuilder<RenameBlueTuskUserDefinedTypeOperation> RenameBlueTuskUserDefinedType(
        this MigrationBuilder migrationBuilder,
        BlueTuskUserDefinedTypeKind kind,
        string name,
        string newName,
        string? schema = null,
        string? newSchema = null) =>
        Add(migrationBuilder, new RenameBlueTuskUserDefinedTypeOperation
        {
            Kind = kind,
            Name = name,
            Schema = schema,
            NewName = newName,
            NewSchema = newSchema,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskEnumTypeOperation> CreateBlueTuskEnumType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateBlueTuskEnumType(migrationBuilder, BlueTuskUserDefinedTypeMetadata.DeserializeEnum(serializedDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterBlueTuskEnumTypeOperation> AlterBlueTuskEnumType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        AlterBlueTuskEnumType(
            migrationBuilder,
            BlueTuskUserDefinedTypeMetadata.DeserializeEnum(serializedDefinition),
            BlueTuskUserDefinedTypeMetadata.DeserializeEnum(serializedOldDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskDomainTypeOperation> CreateBlueTuskDomainType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateBlueTuskDomainType(migrationBuilder, BlueTuskUserDefinedTypeMetadata.DeserializeDomain(serializedDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterBlueTuskDomainTypeOperation> AlterBlueTuskDomainType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        AlterBlueTuskDomainType(
            migrationBuilder,
            BlueTuskUserDefinedTypeMetadata.DeserializeDomain(serializedDefinition),
            BlueTuskUserDefinedTypeMetadata.DeserializeDomain(serializedOldDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskCompositeTypeOperation> CreateBlueTuskCompositeType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateBlueTuskCompositeType(
            migrationBuilder,
            BlueTuskUserDefinedTypeMetadata.DeserializeComposite(serializedDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterBlueTuskCompositeTypeOperation> AlterBlueTuskCompositeType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        AlterBlueTuskCompositeType(
            migrationBuilder,
            BlueTuskUserDefinedTypeMetadata.DeserializeComposite(serializedDefinition),
            BlueTuskUserDefinedTypeMetadata.DeserializeComposite(serializedOldDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskRangeTypeOperation> CreateBlueTuskRangeType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateBlueTuskRangeType(
            migrationBuilder,
            BlueTuskUserDefinedTypeMetadata.DeserializeRange(serializedDefinition));

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

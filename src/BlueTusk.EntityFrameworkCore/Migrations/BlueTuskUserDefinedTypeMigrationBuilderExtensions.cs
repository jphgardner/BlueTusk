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
    public static OperationBuilder<CreateEnumTypeOperation> CreateEnumType(
        this MigrationBuilder migrationBuilder,
        BlueTuskEnumTypeDefinition definition) =>
        Add(migrationBuilder, new CreateEnumTypeOperation { Definition = definition });

    public static OperationBuilder<AlterEnumTypeOperation> AlterEnumType(
        this MigrationBuilder migrationBuilder,
        BlueTuskEnumTypeDefinition definition,
        BlueTuskEnumTypeDefinition oldDefinition) =>
        Add(migrationBuilder, new AlterEnumTypeOperation
        {
            Definition = definition,
            OldDefinition = oldDefinition,
        });

    public static OperationBuilder<DropEnumTypeOperation> DropEnumType(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null) =>
        Add(migrationBuilder, new DropEnumTypeOperation
        {
            Name = name,
            Schema = schema,
            IsDestructiveChange = true,
        });

    public static OperationBuilder<CreateDomainTypeOperation> CreateDomainType(
        this MigrationBuilder migrationBuilder,
        BlueTuskDomainTypeDefinition definition) =>
        Add(migrationBuilder, new CreateDomainTypeOperation { Definition = definition });

    public static OperationBuilder<AlterDomainTypeOperation> AlterDomainType(
        this MigrationBuilder migrationBuilder,
        BlueTuskDomainTypeDefinition definition,
        BlueTuskDomainTypeDefinition oldDefinition) =>
        Add(migrationBuilder, new AlterDomainTypeOperation
        {
            Definition = definition,
            OldDefinition = oldDefinition,
        });

    public static OperationBuilder<DropDomainTypeOperation> DropDomainType(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null) =>
        Add(migrationBuilder, new DropDomainTypeOperation
        {
            Name = name,
            Schema = schema,
            IsDestructiveChange = true,
        });

    public static OperationBuilder<CreateCompositeTypeOperation> CreateCompositeType(
        this MigrationBuilder migrationBuilder,
        BlueTuskCompositeTypeDefinition definition) =>
        Add(migrationBuilder, new CreateCompositeTypeOperation { Definition = definition });

    public static OperationBuilder<AlterCompositeTypeOperation> AlterCompositeType(
        this MigrationBuilder migrationBuilder,
        BlueTuskCompositeTypeDefinition definition,
        BlueTuskCompositeTypeDefinition oldDefinition) =>
        Add(migrationBuilder, new AlterCompositeTypeOperation
        {
            Definition = definition,
            OldDefinition = oldDefinition,
            IsDestructiveChange = BlueTuskUserDefinedTypeAlterationPlanner
                .PlanComposite(oldDefinition, definition)
                .Any(change => change.Kind == CompositeAttributeChangeKind.Drop),
        });

    public static OperationBuilder<DropCompositeTypeOperation> DropCompositeType(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null) =>
        Add(migrationBuilder, new DropCompositeTypeOperation
        {
            Name = name,
            Schema = schema,
            IsDestructiveChange = true,
        });

    public static OperationBuilder<CreateRangeTypeOperation> CreateRangeType(
        this MigrationBuilder migrationBuilder,
        BlueTuskRangeTypeDefinition definition) =>
        Add(migrationBuilder, new CreateRangeTypeOperation { Definition = definition });

    public static OperationBuilder<DropRangeTypeOperation> DropRangeType(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null) =>
        Add(migrationBuilder, new DropRangeTypeOperation
        {
            Name = name,
            Schema = schema,
            IsDestructiveChange = true,
        });

    public static OperationBuilder<RenameRangeTypeOperation> RenameRangeType(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName,
        string multirangeName,
        string newMultirangeName,
        string? schema = null,
        string? newSchema = null,
        string? multirangeSchema = null,
        string? newMultirangeSchema = null) =>
        Add(migrationBuilder, new RenameRangeTypeOperation
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

    public static OperationBuilder<RenameUserDefinedTypeOperation> RenameUserDefinedType(
        this MigrationBuilder migrationBuilder,
        BlueTuskUserDefinedTypeKind kind,
        string name,
        string newName,
        string? schema = null,
        string? newSchema = null) =>
        Add(migrationBuilder, new RenameUserDefinedTypeOperation
        {
            Kind = kind,
            Name = name,
            Schema = schema,
            NewName = newName,
            NewSchema = newSchema,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateEnumTypeOperation> CreateEnumType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateEnumType(migrationBuilder, BlueTuskUserDefinedTypeMetadata.DeserializeEnum(serializedDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterEnumTypeOperation> AlterEnumType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        AlterEnumType(
            migrationBuilder,
            BlueTuskUserDefinedTypeMetadata.DeserializeEnum(serializedDefinition),
            BlueTuskUserDefinedTypeMetadata.DeserializeEnum(serializedOldDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateDomainTypeOperation> CreateDomainType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateDomainType(migrationBuilder, BlueTuskUserDefinedTypeMetadata.DeserializeDomain(serializedDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterDomainTypeOperation> AlterDomainType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        AlterDomainType(
            migrationBuilder,
            BlueTuskUserDefinedTypeMetadata.DeserializeDomain(serializedDefinition),
            BlueTuskUserDefinedTypeMetadata.DeserializeDomain(serializedOldDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateCompositeTypeOperation> CreateCompositeType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateCompositeType(
            migrationBuilder,
            BlueTuskUserDefinedTypeMetadata.DeserializeComposite(serializedDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterCompositeTypeOperation> AlterCompositeType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        string serializedOldDefinition) =>
        AlterCompositeType(
            migrationBuilder,
            BlueTuskUserDefinedTypeMetadata.DeserializeComposite(serializedDefinition),
            BlueTuskUserDefinedTypeMetadata.DeserializeComposite(serializedOldDefinition));

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateRangeTypeOperation> CreateRangeType(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition) =>
        CreateRangeType(
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

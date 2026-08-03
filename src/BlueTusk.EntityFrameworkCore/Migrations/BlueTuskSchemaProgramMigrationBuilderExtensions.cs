using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.SchemaPrograms;
using BlueTusk.EntityFrameworkCore.SchemaPrograms.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

public static class BlueTuskSchemaProgramMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskOperatorOperation> CreateBlueTuskOperator(
        this MigrationBuilder builder, BlueTuskOperatorDefinition definition) =>
        Add(builder, new CreateBlueTuskOperatorOperation { Definition = Validate(definition) });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskOperatorOperation> CreateBlueTuskOperator(
        this MigrationBuilder builder, string definition) =>
        CreateBlueTuskOperator(builder, Deserialize<BlueTuskOperatorDefinition>(definition));

    public static OperationBuilder<ReplaceBlueTuskOperatorOperation> ReplaceBlueTuskOperator(
        this MigrationBuilder builder, BlueTuskOperatorDefinition oldDefinition, BlueTuskOperatorDefinition definition) =>
        Add(builder, new ReplaceBlueTuskOperatorOperation
        {
            OldDefinition = Validate(oldDefinition),
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<ReplaceBlueTuskOperatorOperation> ReplaceBlueTuskOperator(
        this MigrationBuilder builder, string oldDefinition, string definition) =>
        ReplaceBlueTuskOperator(builder, Deserialize<BlueTuskOperatorDefinition>(oldDefinition),
            Deserialize<BlueTuskOperatorDefinition>(definition));

    public static OperationBuilder<DropBlueTuskOperatorOperation> DropBlueTuskOperator(
        this MigrationBuilder builder, BlueTuskOperatorDefinition definition) =>
        Add(builder, new DropBlueTuskOperatorOperation { Definition = Validate(definition), IsDestructiveChange = true });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<DropBlueTuskOperatorOperation> DropBlueTuskOperator(
        this MigrationBuilder builder, string definition) =>
        DropBlueTuskOperator(builder, Deserialize<BlueTuskOperatorDefinition>(definition));

    public static OperationBuilder<CreateBlueTuskOperatorFamilyOperation> CreateBlueTuskOperatorFamily(
        this MigrationBuilder builder, BlueTuskOperatorFamilyDefinition definition) =>
        Add(builder, new CreateBlueTuskOperatorFamilyOperation { Definition = Validate(definition) });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskOperatorFamilyOperation> CreateBlueTuskOperatorFamily(
        this MigrationBuilder builder, string definition) =>
        CreateBlueTuskOperatorFamily(builder, Deserialize<BlueTuskOperatorFamilyDefinition>(definition));

    public static OperationBuilder<AlterBlueTuskOperatorFamilyOperation> AlterBlueTuskOperatorFamily(
        this MigrationBuilder builder, BlueTuskOperatorFamilyDefinition oldDefinition,
        BlueTuskOperatorFamilyDefinition definition) =>
        Add(builder, new AlterBlueTuskOperatorFamilyOperation
        {
            OldDefinition = Validate(oldDefinition),
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterBlueTuskOperatorFamilyOperation> AlterBlueTuskOperatorFamily(
        this MigrationBuilder builder, string oldDefinition, string definition) =>
        AlterBlueTuskOperatorFamily(builder, Deserialize<BlueTuskOperatorFamilyDefinition>(oldDefinition),
            Deserialize<BlueTuskOperatorFamilyDefinition>(definition));

    public static OperationBuilder<DropBlueTuskOperatorFamilyOperation> DropBlueTuskOperatorFamily(
        this MigrationBuilder builder, BlueTuskOperatorFamilyDefinition definition) =>
        Add(builder, new DropBlueTuskOperatorFamilyOperation
        {
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<DropBlueTuskOperatorFamilyOperation> DropBlueTuskOperatorFamily(
        this MigrationBuilder builder, string definition) =>
        DropBlueTuskOperatorFamily(builder, Deserialize<BlueTuskOperatorFamilyDefinition>(definition));

    public static OperationBuilder<CreateBlueTuskOperatorClassOperation> CreateBlueTuskOperatorClass(
        this MigrationBuilder builder, BlueTuskOperatorClassDefinition definition) =>
        Add(builder, new CreateBlueTuskOperatorClassOperation { Definition = Validate(definition) });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskOperatorClassOperation> CreateBlueTuskOperatorClass(
        this MigrationBuilder builder, string definition) =>
        CreateBlueTuskOperatorClass(builder, Deserialize<BlueTuskOperatorClassDefinition>(definition));

    public static OperationBuilder<ReplaceBlueTuskOperatorClassOperation> ReplaceBlueTuskOperatorClass(
        this MigrationBuilder builder, BlueTuskOperatorClassDefinition oldDefinition,
        BlueTuskOperatorClassDefinition definition) =>
        Add(builder, new ReplaceBlueTuskOperatorClassOperation
        {
            OldDefinition = Validate(oldDefinition),
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<ReplaceBlueTuskOperatorClassOperation> ReplaceBlueTuskOperatorClass(
        this MigrationBuilder builder, string oldDefinition, string definition) =>
        ReplaceBlueTuskOperatorClass(builder, Deserialize<BlueTuskOperatorClassDefinition>(oldDefinition),
            Deserialize<BlueTuskOperatorClassDefinition>(definition));

    public static OperationBuilder<DropBlueTuskOperatorClassOperation> DropBlueTuskOperatorClass(
        this MigrationBuilder builder, BlueTuskOperatorClassDefinition definition) =>
        Add(builder, new DropBlueTuskOperatorClassOperation
        {
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<DropBlueTuskOperatorClassOperation> DropBlueTuskOperatorClass(
        this MigrationBuilder builder, string definition) =>
        DropBlueTuskOperatorClass(builder, Deserialize<BlueTuskOperatorClassDefinition>(definition));

    public static OperationBuilder<CreateBlueTuskCastOperation> CreateBlueTuskCast(
        this MigrationBuilder builder, BlueTuskCastDefinition definition) =>
        Add(builder, new CreateBlueTuskCastOperation { Definition = Validate(definition) });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskCastOperation> CreateBlueTuskCast(
        this MigrationBuilder builder, string definition) =>
        CreateBlueTuskCast(builder, Deserialize<BlueTuskCastDefinition>(definition));

    public static OperationBuilder<ReplaceBlueTuskCastOperation> ReplaceBlueTuskCast(
        this MigrationBuilder builder, BlueTuskCastDefinition oldDefinition, BlueTuskCastDefinition definition) =>
        Add(builder, new ReplaceBlueTuskCastOperation
        {
            OldDefinition = Validate(oldDefinition),
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<ReplaceBlueTuskCastOperation> ReplaceBlueTuskCast(
        this MigrationBuilder builder, string oldDefinition, string definition) =>
        ReplaceBlueTuskCast(builder, Deserialize<BlueTuskCastDefinition>(oldDefinition),
            Deserialize<BlueTuskCastDefinition>(definition));

    public static OperationBuilder<DropBlueTuskCastOperation> DropBlueTuskCast(
        this MigrationBuilder builder, BlueTuskCastDefinition definition) =>
        Add(builder, new DropBlueTuskCastOperation { Definition = Validate(definition), IsDestructiveChange = true });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<DropBlueTuskCastOperation> DropBlueTuskCast(
        this MigrationBuilder builder, string definition) =>
        DropBlueTuskCast(builder, Deserialize<BlueTuskCastDefinition>(definition));

    public static OperationBuilder<CreateBlueTuskAggregateOperation> CreateBlueTuskAggregate(
        this MigrationBuilder builder, BlueTuskAggregateDefinition definition) =>
        Add(builder, new CreateBlueTuskAggregateOperation { Definition = Validate(definition) });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskAggregateOperation> CreateBlueTuskAggregate(
        this MigrationBuilder builder, string definition) =>
        CreateBlueTuskAggregate(builder, Deserialize<BlueTuskAggregateDefinition>(definition));

    public static OperationBuilder<ReplaceBlueTuskAggregateOperation> ReplaceBlueTuskAggregate(
        this MigrationBuilder builder, BlueTuskAggregateDefinition oldDefinition,
        BlueTuskAggregateDefinition definition) =>
        Add(builder, new ReplaceBlueTuskAggregateOperation
        {
            OldDefinition = Validate(oldDefinition),
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<ReplaceBlueTuskAggregateOperation> ReplaceBlueTuskAggregate(
        this MigrationBuilder builder, string oldDefinition, string definition) =>
        ReplaceBlueTuskAggregate(builder, Deserialize<BlueTuskAggregateDefinition>(oldDefinition),
            Deserialize<BlueTuskAggregateDefinition>(definition));

    public static OperationBuilder<DropBlueTuskAggregateOperation> DropBlueTuskAggregate(
        this MigrationBuilder builder, BlueTuskAggregateDefinition definition) =>
        Add(builder, new DropBlueTuskAggregateOperation
        {
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<DropBlueTuskAggregateOperation> DropBlueTuskAggregate(
        this MigrationBuilder builder, string definition) =>
        DropBlueTuskAggregate(builder, Deserialize<BlueTuskAggregateDefinition>(definition));

    private static T Validate<T>(T definition)
    {
        _ = BlueTuskSchemaProgramMetadata.Serialize(definition);
        return definition;
    }

    private static T Deserialize<T>(string definition) =>
        BlueTuskSchemaProgramMetadata.DeserializeDefinition<T>(definition);

    private static OperationBuilder<TOperation> Add<TOperation>(MigrationBuilder builder, TOperation operation)
        where TOperation : MigrationOperation
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Operations.Add(operation);
        return new OperationBuilder<TOperation>(operation);
    }
}

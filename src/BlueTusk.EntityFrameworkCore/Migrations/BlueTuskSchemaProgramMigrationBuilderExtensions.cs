using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.SchemaPrograms;
using BlueTusk.EntityFrameworkCore.SchemaPrograms.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

public static class BlueTuskSchemaProgramMigrationBuilderExtensions
{
    public static OperationBuilder<CreateOperatorOperation> CreateOperator(
        this MigrationBuilder builder, BlueTuskOperatorDefinition definition) =>
        Add(builder, new CreateOperatorOperation { Definition = Validate(definition) });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateOperatorOperation> CreateOperator(
        this MigrationBuilder builder, string definition) =>
        CreateOperator(builder, Deserialize<BlueTuskOperatorDefinition>(definition));

    public static OperationBuilder<ReplaceOperatorOperation> ReplaceOperator(
        this MigrationBuilder builder, BlueTuskOperatorDefinition oldDefinition, BlueTuskOperatorDefinition definition) =>
        Add(builder, new ReplaceOperatorOperation
        {
            OldDefinition = Validate(oldDefinition),
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<ReplaceOperatorOperation> ReplaceOperator(
        this MigrationBuilder builder, string oldDefinition, string definition) =>
        ReplaceOperator(builder, Deserialize<BlueTuskOperatorDefinition>(oldDefinition),
            Deserialize<BlueTuskOperatorDefinition>(definition));

    public static OperationBuilder<DropOperatorOperation> DropOperator(
        this MigrationBuilder builder, BlueTuskOperatorDefinition definition) =>
        Add(builder, new DropOperatorOperation { Definition = Validate(definition), IsDestructiveChange = true });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<DropOperatorOperation> DropOperator(
        this MigrationBuilder builder, string definition) =>
        DropOperator(builder, Deserialize<BlueTuskOperatorDefinition>(definition));

    public static OperationBuilder<CreateOperatorFamilyOperation> CreateOperatorFamily(
        this MigrationBuilder builder, BlueTuskOperatorFamilyDefinition definition) =>
        Add(builder, new CreateOperatorFamilyOperation { Definition = Validate(definition) });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateOperatorFamilyOperation> CreateOperatorFamily(
        this MigrationBuilder builder, string definition) =>
        CreateOperatorFamily(builder, Deserialize<BlueTuskOperatorFamilyDefinition>(definition));

    public static OperationBuilder<AlterOperatorFamilyOperation> AlterOperatorFamily(
        this MigrationBuilder builder, BlueTuskOperatorFamilyDefinition oldDefinition,
        BlueTuskOperatorFamilyDefinition definition) =>
        Add(builder, new AlterOperatorFamilyOperation
        {
            OldDefinition = Validate(oldDefinition),
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<AlterOperatorFamilyOperation> AlterOperatorFamily(
        this MigrationBuilder builder, string oldDefinition, string definition) =>
        AlterOperatorFamily(builder, Deserialize<BlueTuskOperatorFamilyDefinition>(oldDefinition),
            Deserialize<BlueTuskOperatorFamilyDefinition>(definition));

    public static OperationBuilder<DropOperatorFamilyOperation> DropOperatorFamily(
        this MigrationBuilder builder, BlueTuskOperatorFamilyDefinition definition) =>
        Add(builder, new DropOperatorFamilyOperation
        {
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<DropOperatorFamilyOperation> DropOperatorFamily(
        this MigrationBuilder builder, string definition) =>
        DropOperatorFamily(builder, Deserialize<BlueTuskOperatorFamilyDefinition>(definition));

    public static OperationBuilder<CreateOperatorClassOperation> CreateOperatorClass(
        this MigrationBuilder builder, BlueTuskOperatorClassDefinition definition) =>
        Add(builder, new CreateOperatorClassOperation { Definition = Validate(definition) });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateOperatorClassOperation> CreateOperatorClass(
        this MigrationBuilder builder, string definition) =>
        CreateOperatorClass(builder, Deserialize<BlueTuskOperatorClassDefinition>(definition));

    public static OperationBuilder<ReplaceOperatorClassOperation> ReplaceOperatorClass(
        this MigrationBuilder builder, BlueTuskOperatorClassDefinition oldDefinition,
        BlueTuskOperatorClassDefinition definition) =>
        Add(builder, new ReplaceOperatorClassOperation
        {
            OldDefinition = Validate(oldDefinition),
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<ReplaceOperatorClassOperation> ReplaceOperatorClass(
        this MigrationBuilder builder, string oldDefinition, string definition) =>
        ReplaceOperatorClass(builder, Deserialize<BlueTuskOperatorClassDefinition>(oldDefinition),
            Deserialize<BlueTuskOperatorClassDefinition>(definition));

    public static OperationBuilder<DropOperatorClassOperation> DropOperatorClass(
        this MigrationBuilder builder, BlueTuskOperatorClassDefinition definition) =>
        Add(builder, new DropOperatorClassOperation
        {
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<DropOperatorClassOperation> DropOperatorClass(
        this MigrationBuilder builder, string definition) =>
        DropOperatorClass(builder, Deserialize<BlueTuskOperatorClassDefinition>(definition));

    public static OperationBuilder<CreateCastOperation> CreateCast(
        this MigrationBuilder builder, BlueTuskCastDefinition definition) =>
        Add(builder, new CreateCastOperation { Definition = Validate(definition) });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateCastOperation> CreateCast(
        this MigrationBuilder builder, string definition) =>
        CreateCast(builder, Deserialize<BlueTuskCastDefinition>(definition));

    public static OperationBuilder<ReplaceCastOperation> ReplaceCast(
        this MigrationBuilder builder, BlueTuskCastDefinition oldDefinition, BlueTuskCastDefinition definition) =>
        Add(builder, new ReplaceCastOperation
        {
            OldDefinition = Validate(oldDefinition),
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<ReplaceCastOperation> ReplaceCast(
        this MigrationBuilder builder, string oldDefinition, string definition) =>
        ReplaceCast(builder, Deserialize<BlueTuskCastDefinition>(oldDefinition),
            Deserialize<BlueTuskCastDefinition>(definition));

    public static OperationBuilder<DropCastOperation> DropCast(
        this MigrationBuilder builder, BlueTuskCastDefinition definition) =>
        Add(builder, new DropCastOperation { Definition = Validate(definition), IsDestructiveChange = true });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<DropCastOperation> DropCast(
        this MigrationBuilder builder, string definition) =>
        DropCast(builder, Deserialize<BlueTuskCastDefinition>(definition));

    public static OperationBuilder<CreateAggregateOperation> CreateAggregate(
        this MigrationBuilder builder, BlueTuskAggregateDefinition definition) =>
        Add(builder, new CreateAggregateOperation { Definition = Validate(definition) });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateAggregateOperation> CreateAggregate(
        this MigrationBuilder builder, string definition) =>
        CreateAggregate(builder, Deserialize<BlueTuskAggregateDefinition>(definition));

    public static OperationBuilder<ReplaceAggregateOperation> ReplaceAggregate(
        this MigrationBuilder builder, BlueTuskAggregateDefinition oldDefinition,
        BlueTuskAggregateDefinition definition) =>
        Add(builder, new ReplaceAggregateOperation
        {
            OldDefinition = Validate(oldDefinition),
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<ReplaceAggregateOperation> ReplaceAggregate(
        this MigrationBuilder builder, string oldDefinition, string definition) =>
        ReplaceAggregate(builder, Deserialize<BlueTuskAggregateDefinition>(oldDefinition),
            Deserialize<BlueTuskAggregateDefinition>(definition));

    public static OperationBuilder<DropAggregateOperation> DropAggregate(
        this MigrationBuilder builder, BlueTuskAggregateDefinition definition) =>
        Add(builder, new DropAggregateOperation
        {
            Definition = Validate(definition),
            IsDestructiveChange = true,
        });

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<DropAggregateOperation> DropAggregate(
        this MigrationBuilder builder, string definition) =>
        DropAggregate(builder, Deserialize<BlueTuskAggregateDefinition>(definition));

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

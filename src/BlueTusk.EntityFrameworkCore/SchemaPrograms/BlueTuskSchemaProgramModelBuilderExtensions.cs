using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.SchemaPrograms;
using BlueTusk.EntityFrameworkCore.SchemaPrograms.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures PostgreSQL operators, index semantics, casts, and aggregates.</summary>
public static class BlueTuskSchemaProgramModelBuilderExtensions
{
    public static ModelBuilder HasBlueTuskOperator(
        this ModelBuilder modelBuilder,
        string name,
        Action<BlueTuskOperatorBuilder> configure,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new BlueTuskOperatorBuilder(name, schema);
        configure(builder);
        return HasBlueTuskOperator(modelBuilder, builder.Build());
    }

    public static ModelBuilder HasBlueTuskOperator(
        this ModelBuilder modelBuilder,
        BlueTuskOperatorDefinition definition) => SetItem(
        modelBuilder,
        definition,
        set => set.Operators,
        (set, values) => set with { Operators = values },
        BlueTuskSchemaProgramMetadata.OperatorKey.Create);

    public static ModelBuilder HasBlueTuskOperatorFamily(
        this ModelBuilder modelBuilder,
        string name,
        string indexMethod,
        Action<BlueTuskOperatorFamilyBuilder>? configure = null,
        string? schema = null)
    {
        var builder = new BlueTuskOperatorFamilyBuilder(name, schema, indexMethod);
        configure?.Invoke(builder);
        return HasBlueTuskOperatorFamily(modelBuilder, builder.Build());
    }

    public static ModelBuilder HasBlueTuskOperatorFamily(
        this ModelBuilder modelBuilder,
        BlueTuskOperatorFamilyDefinition definition) => SetItem(
        modelBuilder,
        definition,
        set => set.OperatorFamilies,
        (set, values) => set with { OperatorFamilies = values },
        BlueTuskSchemaProgramMetadata.OperatorFamilyKey.Create);

    public static ModelBuilder HasBlueTuskOperatorClass(
        this ModelBuilder modelBuilder,
        string name,
        string dataType,
        string indexMethod,
        Action<BlueTuskOperatorClassBuilder> configure,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new BlueTuskOperatorClassBuilder(name, schema, dataType, indexMethod);
        configure(builder);
        return HasBlueTuskOperatorClass(modelBuilder, builder.Build());
    }

    public static ModelBuilder HasBlueTuskOperatorClass(
        this ModelBuilder modelBuilder,
        BlueTuskOperatorClassDefinition definition) => SetItem(
        modelBuilder,
        definition,
        set => set.OperatorClasses,
        (set, values) => set with { OperatorClasses = values },
        BlueTuskSchemaProgramMetadata.OperatorClassKey.Create);

    public static ModelBuilder HasBlueTuskCast(
        this ModelBuilder modelBuilder,
        string sourceType,
        string targetType,
        Action<BlueTuskCastBuilder>? configure = null)
    {
        var builder = new BlueTuskCastBuilder(sourceType, targetType);
        configure?.Invoke(builder);
        return HasBlueTuskCast(modelBuilder, builder.Build());
    }

    public static ModelBuilder HasBlueTuskCast(
        this ModelBuilder modelBuilder,
        BlueTuskCastDefinition definition) => SetItem(
        modelBuilder,
        definition,
        set => set.Casts,
        (set, values) => set with { Casts = values },
        BlueTuskSchemaProgramMetadata.CastKey.Create);

    public static ModelBuilder HasBlueTuskAggregate(
        this ModelBuilder modelBuilder,
        string name,
        string identityArgumentsSql,
        Action<BlueTuskAggregateBuilder> configure,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new BlueTuskAggregateBuilder(name, schema, identityArgumentsSql);
        configure(builder);
        return HasBlueTuskAggregate(modelBuilder, builder.Build());
    }

    public static ModelBuilder HasBlueTuskAggregate(
        this ModelBuilder modelBuilder,
        BlueTuskAggregateDefinition definition) => SetItem(
        modelBuilder,
        definition,
        set => set.Aggregates,
        (set, values) => set with { Aggregates = values },
        BlueTuskSchemaProgramMetadata.AggregateKey.Create);

    public static BlueTuskSchemaProgramDefinitionSet GetBlueTuskSchemaPrograms(this IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BlueTuskSchemaProgramMetadata.Get(model);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasBlueTuskSchemaPrograms(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return Set(modelBuilder, BlueTuskSchemaProgramMetadata.Deserialize(serializedDefinitions));
    }

    private static ModelBuilder SetItem<T, TKey>(
        ModelBuilder modelBuilder,
        T definition,
        Func<BlueTuskSchemaProgramDefinitionSet, IReadOnlyList<T>> get,
        Func<BlueTuskSchemaProgramDefinitionSet, IReadOnlyList<T>, BlueTuskSchemaProgramDefinitionSet> update,
        Func<T, TKey> key)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var current = BlueTuskSchemaProgramMetadata.Get(modelBuilder.Model);
        var itemKey = key(definition);
        var values = get(current).Where(item => !EqualityComparer<TKey>.Default.Equals(key(item), itemKey))
            .Append(definition)
            .ToArray();
        return Set(modelBuilder, update(current, values));
    }

    private static ModelBuilder Set(ModelBuilder modelBuilder, BlueTuskSchemaProgramDefinitionSet definitions)
    {
        BlueTuskSchemaProgramMetadata.Validate(definitions);
        if (definitions.Operators.Count == 0 && definitions.OperatorFamilies.Count == 0 &&
            definitions.OperatorClasses.Count == 0 && definitions.Casts.Count == 0 &&
            definitions.Aggregates.Count == 0)
        {
            modelBuilder.Model.RemoveAnnotation(BlueTuskSchemaProgramMetadata.AnnotationName);
        }
        else
        {
            modelBuilder.Model.SetAnnotation(
                BlueTuskSchemaProgramMetadata.AnnotationName,
                BlueTuskSchemaProgramMetadata.Serialize(definitions));
        }

        return modelBuilder;
    }
}

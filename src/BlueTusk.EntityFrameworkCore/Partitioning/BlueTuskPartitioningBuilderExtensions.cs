using System.ComponentModel;
using System.Linq.Expressions;
using BlueTusk.EntityFrameworkCore.Partitioning;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

/// <summary>PostgreSQL declarative-partitioning extensions for EF models.</summary>
public static class BlueTuskPartitioningBuilderExtensions
{
    /// <summary>Configures a partitioned table with explicit column or trusted SQL-expression keys.</summary>
    public static BlueTuskPartitioningBuilder HasBlueTuskPartitioning(
        this EntityTypeBuilder entityBuilder,
        BlueTuskPartitionStrategy strategy,
        params BlueTuskPartitionKeyDefinition[] keys)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        ArgumentNullException.ThrowIfNull(keys);
        var definition = new BlueTuskPartitioningDefinition(strategy, keys, []);
        BlueTuskPartitioningBuilder.ValidateDefinition(definition);
        entityBuilder.Metadata.SetAnnotation(
            BlueTuskPartitionMetadata.AnnotationName,
            BlueTuskPartitionMetadata.Serialize(definition));
        return new BlueTuskPartitioningBuilder(entityBuilder.Metadata, []);
    }

    /// <summary>Configures RANGE partitioning over one or more mapped properties.</summary>
    public static BlueTuskPartitioningBuilder HasBlueTuskRangePartitioning<TEntity>(
        this EntityTypeBuilder<TEntity> entityBuilder,
        Expression<Func<TEntity, object?>> keys)
        where TEntity : class =>
        HasPropertyPartitioning(entityBuilder, BlueTuskPartitionStrategy.Range, keys);

    /// <summary>Configures LIST partitioning over one or more mapped properties.</summary>
    public static BlueTuskPartitioningBuilder HasBlueTuskListPartitioning<TEntity>(
        this EntityTypeBuilder<TEntity> entityBuilder,
        Expression<Func<TEntity, object?>> keys)
        where TEntity : class =>
        HasPropertyPartitioning(entityBuilder, BlueTuskPartitionStrategy.List, keys);

    /// <summary>Configures HASH partitioning over one or more mapped properties.</summary>
    public static BlueTuskPartitioningBuilder HasBlueTuskHashPartitioning<TEntity>(
        this EntityTypeBuilder<TEntity> entityBuilder,
        Expression<Func<TEntity, object?>> keys)
        where TEntity : class =>
        HasPropertyPartitioning(entityBuilder, BlueTuskPartitionStrategy.Hash, keys);

    /// <summary>Removes PostgreSQL partitioning metadata from an entity table.</summary>
    public static EntityTypeBuilder HasNoBlueTuskPartitioning(this EntityTypeBuilder entityBuilder)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        entityBuilder.Metadata.RemoveAnnotation(BlueTuskPartitionMetadata.AnnotationName);
        return entityBuilder;
    }

    /// <summary>Reads PostgreSQL partitioning metadata from an EF entity type.</summary>
    public static BlueTuskPartitioningDefinition? GetBlueTuskPartitioning(
        this IReadOnlyEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return BlueTuskPartitionMetadata.Get(entityType);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static EntityTypeBuilder HasBlueTuskPartitioning(
        this EntityTypeBuilder entityBuilder,
        string serializedDefinition)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        var definition = BlueTuskPartitionMetadata.Deserialize(serializedDefinition);
        BlueTuskPartitioningBuilder.ValidateDefinition(definition);
        entityBuilder.Metadata.SetAnnotation(
            BlueTuskPartitionMetadata.AnnotationName,
            BlueTuskPartitionMetadata.Serialize(definition));
        return entityBuilder;
    }

    private static BlueTuskPartitioningBuilder HasPropertyPartitioning<TEntity>(
        EntityTypeBuilder<TEntity> entityBuilder,
        BlueTuskPartitionStrategy strategy,
        Expression<Func<TEntity, object?>> keys)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        ArgumentNullException.ThrowIfNull(keys);
        var definitions = GetPropertyNames(keys)
            .Select(
                propertyName =>
                {
                    _ = entityBuilder.Metadata.FindProperty(propertyName)
                        ?? throw new ArgumentException(
                            $"Property '{propertyName}' is not mapped by entity '{entityBuilder.Metadata.DisplayName()}'.",
                            nameof(keys));
                    return BlueTuskPartitionKeyDefinition.Column(propertyName);
                })
            .ToArray();
        return HasBlueTuskPartitioning(entityBuilder, strategy, definitions);
    }

    private static string[] GetPropertyNames<TEntity>(Expression<Func<TEntity, object?>> expression)
    {
        static string ReadMember(Expression item, ParameterExpression parameter)
        {
            while (item is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            {
                item = unary.Operand;
            }

            return item is MemberExpression { Expression: var target } member && target == parameter
                ? member.Member.Name
                : throw new ArgumentException("The expression must select mapped properties directly.", nameof(expression));
        }

        return expression.Body is NewExpression creation
            ? creation.Arguments.Select(item => ReadMember(item, expression.Parameters[0])).ToArray()
            : [ReadMember(expression.Body, expression.Parameters[0])];
    }
}

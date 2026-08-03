using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.ExpressionIndexes;
using BlueTusk.EntityFrameworkCore.ExpressionIndexes.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

/// <summary>PostgreSQL expression-index configuration for indexes without an EF property-key representation.</summary>
public static class BlueTuskExpressionIndexBuilderExtensions
{
    /// <summary>Adds or replaces a provider-owned PostgreSQL expression or mixed-key index.</summary>
    public static EntityTypeBuilder HasExpressionIndex(
        this EntityTypeBuilder entityBuilder,
        string name,
        Action<BlueTuskExpressionIndexBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new BlueTuskExpressionIndexBuilder(name);
        configure(builder);
        Set(entityBuilder.Metadata, Replace(Get(entityBuilder.Metadata), builder.Build()));
        return entityBuilder;
    }

    /// <summary>Adds or replaces a provider-owned PostgreSQL expression or mixed-key index.</summary>
    public static EntityTypeBuilder<TEntity> HasExpressionIndex<TEntity>(
        this EntityTypeBuilder<TEntity> entityBuilder,
        string name,
        Action<BlueTuskExpressionIndexBuilder> configure)
        where TEntity : class
    {
        HasExpressionIndex((EntityTypeBuilder)entityBuilder, name, configure);
        return entityBuilder;
    }

    /// <summary>Removes a provider-owned expression index by name.</summary>
    public static EntityTypeBuilder HasNoExpressionIndex(
        this EntityTypeBuilder entityBuilder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Set(
            entityBuilder.Metadata,
            Get(entityBuilder.Metadata)
                .Where(definition => !string.Equals(definition.Name, name, StringComparison.Ordinal))
                .ToArray());
        return entityBuilder;
    }

    /// <summary>Reads provider-owned expression indexes from an EF entity type.</summary>
    public static IReadOnlyList<BlueTuskExpressionIndexDefinition> GetExpressionIndexes(
        this IReadOnlyEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return Get(entityType);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static EntityTypeBuilder HasExpressionIndexes(
        this EntityTypeBuilder entityBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        Set(entityBuilder.Metadata, BlueTuskExpressionIndexMetadata.Deserialize(serializedDefinitions));
        return entityBuilder;
    }

    private static IReadOnlyList<BlueTuskExpressionIndexDefinition> Get(IReadOnlyAnnotatable annotatable) =>
        BlueTuskExpressionIndexMetadata.Get(annotatable);

    private static BlueTuskExpressionIndexDefinition[] Replace(
        IReadOnlyList<BlueTuskExpressionIndexDefinition> definitions,
        BlueTuskExpressionIndexDefinition replacement) =>
        definitions.Where(definition => !string.Equals(definition.Name, replacement.Name, StringComparison.Ordinal))
            .Append(replacement)
            .ToArray();

    private static void Set(
        IMutableEntityType entityType,
        IReadOnlyList<BlueTuskExpressionIndexDefinition> definitions)
    {
        if (definitions.Count == 0)
        {
            entityType.RemoveAnnotation(BlueTuskExpressionIndexMetadata.AnnotationName);
            return;
        }

        entityType.SetAnnotation(
            BlueTuskExpressionIndexMetadata.AnnotationName,
            BlueTuskExpressionIndexMetadata.Serialize(definitions));
    }
}

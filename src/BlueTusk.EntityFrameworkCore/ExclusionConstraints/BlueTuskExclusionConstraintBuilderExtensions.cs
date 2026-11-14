using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

/// <summary>PostgreSQL exclusion-constraint extensions for EF entity tables.</summary>
public static class BlueTuskExclusionConstraintBuilderExtensions
{
    /// <summary>Adds or replaces a PostgreSQL exclusion constraint.</summary>
    public static EntityTypeBuilder HasExclusionConstraint(
        this EntityTypeBuilder entityBuilder,
        string name,
        Action<BlueTuskExclusionConstraintBuilder> configure)
    {
        BlueTuskGuard.NotNull(entityBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        BlueTuskGuard.NotNull(configure);
        var builder = new BlueTuskExclusionConstraintBuilder(entityBuilder.Metadata, name);
        configure(builder);
        Set(entityBuilder.Metadata, Replace(BlueTuskExclusionConstraintMetadata.Get(entityBuilder.Metadata), builder.Build()));
        return entityBuilder;
    }

    /// <summary>Adds or replaces a PostgreSQL exclusion constraint using typed property selectors.</summary>
    public static EntityTypeBuilder<TEntity> HasExclusionConstraint<TEntity>(
        this EntityTypeBuilder<TEntity> entityBuilder,
        string name,
        Action<BlueTuskExclusionConstraintBuilder<TEntity>> configure)
        where TEntity : class
    {
        BlueTuskGuard.NotNull(entityBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        BlueTuskGuard.NotNull(configure);
        var builder = new BlueTuskExclusionConstraintBuilder<TEntity>(entityBuilder.Metadata, name);
        configure(builder);
        Set(entityBuilder.Metadata, Replace(BlueTuskExclusionConstraintMetadata.Get(entityBuilder.Metadata), builder.Build()));
        return entityBuilder;
    }

    /// <summary>Removes a PostgreSQL exclusion constraint by name.</summary>
    public static EntityTypeBuilder HasNoExclusionConstraint(
        this EntityTypeBuilder entityBuilder,
        string name)
    {
        BlueTuskGuard.NotNull(entityBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        Set(
            entityBuilder.Metadata,
            BlueTuskExclusionConstraintMetadata.Get(entityBuilder.Metadata)
                .Where(definition => !string.Equals(definition.Name, name, StringComparison.Ordinal))
                .ToArray());
        return entityBuilder;
    }

    /// <summary>Reads provider-owned exclusion constraints from an EF entity type.</summary>
    public static IReadOnlyList<BlueTuskExclusionConstraintDefinition> GetExclusionConstraints(
        this IReadOnlyEntityType entityType)
    {
        BlueTuskGuard.NotNull(entityType);
        return BlueTuskExclusionConstraintMetadata.Get(entityType);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static EntityTypeBuilder HasExclusionConstraints(
        this EntityTypeBuilder entityBuilder,
        string serializedDefinitions)
    {
        BlueTuskGuard.NotNull(entityBuilder);
        Set(entityBuilder.Metadata, BlueTuskExclusionConstraintMetadata.Deserialize(serializedDefinitions));
        return entityBuilder;
    }

    private static BlueTuskExclusionConstraintDefinition[] Replace(
        IReadOnlyList<BlueTuskExclusionConstraintDefinition> definitions,
        BlueTuskExclusionConstraintDefinition replacement) =>
        definitions.Where(definition => !string.Equals(definition.Name, replacement.Name, StringComparison.Ordinal))
            .Append(replacement)
            .ToArray();

    private static void Set(
        IMutableEntityType entityType,
        IReadOnlyList<BlueTuskExclusionConstraintDefinition> definitions)
    {
        if (definitions.Count == 0)
        {
            entityType.RemoveAnnotation(BlueTuskExclusionConstraintMetadata.AnnotationName);
            return;
        }

        entityType.SetAnnotation(
            BlueTuskExclusionConstraintMetadata.AnnotationName,
            BlueTuskExclusionConstraintMetadata.Serialize(definitions));
    }
}

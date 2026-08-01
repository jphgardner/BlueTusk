using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Triggers;
using BlueTusk.EntityFrameworkCore.Triggers.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

public static class BlueTuskTriggerBuilderExtensions
{
    public static EntityTypeBuilder HasBlueTuskTrigger(
        this EntityTypeBuilder entityBuilder,
        string name,
        Action<BlueTuskTriggerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new BlueTuskTriggerBuilder(entityBuilder.Metadata, name);
        configure(builder);
        Set(entityBuilder.Metadata, Replace(BlueTuskTriggerMetadata.Get(entityBuilder.Metadata), builder.Build()));
        return entityBuilder;
    }

    public static EntityTypeBuilder<TEntity> HasBlueTuskTrigger<TEntity>(
        this EntityTypeBuilder<TEntity> entityBuilder,
        string name,
        Action<BlueTuskTriggerBuilder<TEntity>> configure)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new BlueTuskTriggerBuilder<TEntity>(entityBuilder.Metadata, name);
        configure(builder);
        Set(entityBuilder.Metadata, Replace(BlueTuskTriggerMetadata.Get(entityBuilder.Metadata), builder.Build()));
        return entityBuilder;
    }

    public static EntityTypeBuilder HasNoBlueTuskTrigger(this EntityTypeBuilder entityBuilder, string name)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Set(
            entityBuilder.Metadata,
            BlueTuskTriggerMetadata.Get(entityBuilder.Metadata)
                .Where(item => !string.Equals(item.Name, name, StringComparison.Ordinal))
                .ToArray());
        return entityBuilder;
    }

    public static IReadOnlyList<BlueTuskTriggerDefinition> GetBlueTuskTriggers(this IReadOnlyEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return BlueTuskTriggerMetadata.Get(entityType);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static EntityTypeBuilder HasBlueTuskTriggers(
        this EntityTypeBuilder entityBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        Set(entityBuilder.Metadata, BlueTuskTriggerMetadata.Deserialize(serializedDefinitions));
        return entityBuilder;
    }

    private static BlueTuskTriggerDefinition[] Replace(
        IReadOnlyList<BlueTuskTriggerDefinition> definitions,
        BlueTuskTriggerDefinition replacement) =>
        definitions.Where(item => !string.Equals(item.Name, replacement.Name, StringComparison.Ordinal))
            .Append(replacement)
            .ToArray();

    private static void Set(IMutableEntityType entityType, IReadOnlyList<BlueTuskTriggerDefinition> definitions)
    {
        if (definitions.Count == 0)
        {
            entityType.RemoveAnnotation(BlueTuskTriggerMetadata.AnnotationName);
            return;
        }

        entityType.SetAnnotation(BlueTuskTriggerMetadata.AnnotationName, BlueTuskTriggerMetadata.Serialize(definitions));
    }
}

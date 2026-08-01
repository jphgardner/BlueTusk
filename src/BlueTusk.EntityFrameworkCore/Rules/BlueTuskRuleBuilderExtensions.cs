using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Rules;
using BlueTusk.EntityFrameworkCore.Rules.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

public static class BlueTuskRuleBuilderExtensions
{
    public static EntityTypeBuilder HasBlueTuskRule(
        this EntityTypeBuilder entityBuilder,
        string name,
        BlueTuskRuleEvent @event,
        string actionSql,
        bool instead = false,
        string? conditionSql = null,
        BlueTuskRuleEnabledMode enabledMode = BlueTuskRuleEnabledMode.Origin)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var definition = new BlueTuskRuleDefinition(
            name,
            @event,
            instead,
            conditionSql,
            actionSql,
            enabledMode);
        var rules = BlueTuskRuleMetadata.Get(entityBuilder.Metadata)
            .Where(item => !string.Equals(item.Name, name, StringComparison.Ordinal))
            .Append(definition)
            .ToArray();
        Set(entityBuilder.Metadata, rules);
        return entityBuilder;
    }

    public static EntityTypeBuilder<TEntity> HasBlueTuskRule<TEntity>(
        this EntityTypeBuilder<TEntity> entityBuilder,
        string name,
        BlueTuskRuleEvent @event,
        string actionSql,
        bool instead = false,
        string? conditionSql = null,
        BlueTuskRuleEnabledMode enabledMode = BlueTuskRuleEnabledMode.Origin)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        HasBlueTuskRule(
            (EntityTypeBuilder)entityBuilder,
            name,
            @event,
            actionSql,
            instead,
            conditionSql,
            enabledMode);
        return entityBuilder;
    }

    public static EntityTypeBuilder HasNoBlueTuskRule(this EntityTypeBuilder entityBuilder, string name)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Set(
            entityBuilder.Metadata,
            BlueTuskRuleMetadata.Get(entityBuilder.Metadata)
                .Where(item => !string.Equals(item.Name, name, StringComparison.Ordinal))
                .ToArray());
        return entityBuilder;
    }

    public static IReadOnlyList<BlueTuskRuleDefinition> GetBlueTuskRules(this IReadOnlyEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return BlueTuskRuleMetadata.Get(entityType);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static EntityTypeBuilder HasBlueTuskRules(this EntityTypeBuilder entityBuilder, string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        Set(entityBuilder.Metadata, BlueTuskRuleMetadata.Deserialize(serializedDefinitions));
        return entityBuilder;
    }

    private static void Set(IMutableEntityType entityType, IReadOnlyList<BlueTuskRuleDefinition> definitions)
    {
        if (definitions.Count == 0)
        {
            entityType.RemoveAnnotation(BlueTuskRuleMetadata.AnnotationName);
        }
        else
        {
            entityType.SetAnnotation(BlueTuskRuleMetadata.AnnotationName, BlueTuskRuleMetadata.Serialize(definitions));
        }
    }
}

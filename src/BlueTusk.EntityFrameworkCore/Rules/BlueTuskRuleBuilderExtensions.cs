using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Rules;
using BlueTusk.EntityFrameworkCore.Rules.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

public static class BlueTuskRuleBuilderExtensions
{
    public static EntityTypeBuilder HasRule(
        this EntityTypeBuilder entityBuilder,
        string name,
        BlueTuskRuleEvent @event,
        string actionSql,
        bool instead = false,
        string? conditionSql = null,
        BlueTuskRuleEnabledMode enabledMode = BlueTuskRuleEnabledMode.Origin)
    {
        BlueTuskGuard.NotNull(entityBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
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

    public static EntityTypeBuilder<TEntity> HasRule<TEntity>(
        this EntityTypeBuilder<TEntity> entityBuilder,
        string name,
        BlueTuskRuleEvent @event,
        string actionSql,
        bool instead = false,
        string? conditionSql = null,
        BlueTuskRuleEnabledMode enabledMode = BlueTuskRuleEnabledMode.Origin)
        where TEntity : class
    {
        BlueTuskGuard.NotNull(entityBuilder);
        HasRule(
            (EntityTypeBuilder)entityBuilder,
            name,
            @event,
            actionSql,
            instead,
            conditionSql,
            enabledMode);
        return entityBuilder;
    }

    public static EntityTypeBuilder HasNoRule(this EntityTypeBuilder entityBuilder, string name)
    {
        BlueTuskGuard.NotNull(entityBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        Set(
            entityBuilder.Metadata,
            BlueTuskRuleMetadata.Get(entityBuilder.Metadata)
                .Where(item => !string.Equals(item.Name, name, StringComparison.Ordinal))
                .ToArray());
        return entityBuilder;
    }

    public static IReadOnlyList<BlueTuskRuleDefinition> GetRules(this IReadOnlyEntityType entityType)
    {
        BlueTuskGuard.NotNull(entityType);
        return BlueTuskRuleMetadata.Get(entityType);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static EntityTypeBuilder HasRules(this EntityTypeBuilder entityBuilder, string serializedDefinitions)
    {
        BlueTuskGuard.NotNull(entityBuilder);
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

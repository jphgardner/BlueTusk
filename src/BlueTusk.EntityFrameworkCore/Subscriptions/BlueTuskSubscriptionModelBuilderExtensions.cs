using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Subscriptions;
using BlueTusk.EntityFrameworkCore.Subscriptions.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Microsoft.EntityFrameworkCore;

public static class BlueTuskSubscriptionModelBuilderExtensions
{
    public static ModelBuilder HasSubscription(
        this ModelBuilder modelBuilder,
        string name,
        Action<BlueTuskSubscriptionBuilder> configure)
    {
        BlueTuskGuard.NotNull(modelBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        BlueTuskGuard.NotNull(configure);
        var builder = new BlueTuskSubscriptionBuilder(name);
        configure(builder);
        return HasSubscription(modelBuilder, builder.Build());
    }

    public static ModelBuilder HasSubscription(
        this ModelBuilder modelBuilder,
        BlueTuskSubscriptionDefinition definition)
    {
        BlueTuskGuard.NotNull(modelBuilder);
        BlueTuskSubscriptionMetadata.ValidateForModel(definition);
        definition = BlueTuskSubscriptionMetadata.Normalize(definition);
        var definitions = BlueTuskSubscriptionMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskSubscriptionDefinitionSet(
            definitions.Subscriptions
                .Where(item => !string.Equals(item.Name, definition.Name, StringComparison.Ordinal))
                .Append(definition)
                .ToArray()));
    }

    public static ModelBuilder HasNoSubscription(this ModelBuilder modelBuilder, string name)
    {
        BlueTuskGuard.NotNull(modelBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        var definitions = BlueTuskSubscriptionMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskSubscriptionDefinitionSet(
            definitions.Subscriptions
                .Where(item => !string.Equals(item.Name, name, StringComparison.Ordinal))
                .ToArray()));
    }

    public static BlueTuskSubscriptionDefinitionSet GetSubscriptions(this IReadOnlyModel model)
    {
        BlueTuskGuard.NotNull(model);
        return BlueTuskSubscriptionMetadata.Get(model);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasSubscriptions(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        BlueTuskGuard.NotNull(modelBuilder);
        return Set(modelBuilder, BlueTuskSubscriptionMetadata.Deserialize(serializedDefinitions));
    }

    private static ModelBuilder Set(ModelBuilder modelBuilder, BlueTuskSubscriptionDefinitionSet definitions)
    {
        if (definitions.Subscriptions.Count == 0)
        {
            modelBuilder.Model.RemoveAnnotation(BlueTuskSubscriptionMetadata.AnnotationName);
        }
        else
        {
            modelBuilder.Model.SetAnnotation(
                BlueTuskSubscriptionMetadata.AnnotationName,
                BlueTuskSubscriptionMetadata.Serialize(definitions));
        }

        return modelBuilder;
    }
}

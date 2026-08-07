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
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new BlueTuskSubscriptionBuilder(name);
        configure(builder);
        return HasSubscription(modelBuilder, builder.Build());
    }

    public static ModelBuilder HasSubscription(
        this ModelBuilder modelBuilder,
        BlueTuskSubscriptionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
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
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var definitions = BlueTuskSubscriptionMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskSubscriptionDefinitionSet(
            definitions.Subscriptions
                .Where(item => !string.Equals(item.Name, name, StringComparison.Ordinal))
                .ToArray()));
    }

    public static BlueTuskSubscriptionDefinitionSet GetSubscriptions(this IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BlueTuskSubscriptionMetadata.Get(model);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasSubscriptions(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
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

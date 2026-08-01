using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.EventTriggers;
using BlueTusk.EntityFrameworkCore.EventTriggers.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures provider-owned PostgreSQL event triggers.</summary>
public static class BlueTuskEventTriggerModelBuilderExtensions
{
    /// <summary>Adds or replaces a database-level PostgreSQL event trigger.</summary>
    public static ModelBuilder HasBlueTuskEventTrigger(
        this ModelBuilder modelBuilder,
        string name,
        BlueTuskEventTriggerEvent @event,
        string functionName,
        Action<BlueTuskEventTriggerBuilder>? configure = null,
        string? functionSchema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var builder = new BlueTuskEventTriggerBuilder(name, @event, functionName, functionSchema);
        configure?.Invoke(builder);
        return HasBlueTuskEventTrigger(modelBuilder, builder.Build());
    }

    /// <summary>Adds or replaces a canonical event-trigger definition.</summary>
    public static ModelBuilder HasBlueTuskEventTrigger(
        this ModelBuilder modelBuilder,
        BlueTuskEventTriggerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        BlueTuskEventTriggerMetadata.Validate(definition);
        definition = BlueTuskEventTriggerMetadata.Normalize(definition);
        var definitions = BlueTuskEventTriggerMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskEventTriggerDefinitionSet(
            definitions.EventTriggers.Where(item => item.Name != definition.Name)
                .Append(definition)
                .ToArray()));
    }

    /// <summary>Reads all provider-owned event triggers from the model.</summary>
    public static BlueTuskEventTriggerDefinitionSet GetBlueTuskEventTriggers(this IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BlueTuskEventTriggerMetadata.Get(model);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasBlueTuskEventTriggers(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return Set(modelBuilder, BlueTuskEventTriggerMetadata.Deserialize(serializedDefinitions));
    }

    private static ModelBuilder Set(ModelBuilder modelBuilder, BlueTuskEventTriggerDefinitionSet definitions)
    {
        BlueTuskEventTriggerMetadata.Validate(definitions);
        if (definitions.EventTriggers.Count == 0)
        {
            modelBuilder.Model.RemoveAnnotation(BlueTuskEventTriggerMetadata.AnnotationName);
        }
        else
        {
            modelBuilder.Model.SetAnnotation(
                BlueTuskEventTriggerMetadata.AnnotationName,
                BlueTuskEventTriggerMetadata.Serialize(definitions));
        }

        return modelBuilder;
    }
}

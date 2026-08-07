using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Extensions;
using BlueTusk.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Model-builder extensions for provider-owned PostgreSQL extensions.</summary>
public static class BlueTuskExtensionModelBuilderExtensions
{
    /// <summary>Adds or replaces a PostgreSQL extension installation in the model.</summary>
    public static ModelBuilder HasExtension(
        this ModelBuilder modelBuilder,
        string name,
        Action<BlueTuskExtensionBuilder>? buildAction = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var builder = new BlueTuskExtensionBuilder(name);
        buildAction?.Invoke(builder);
        return HasExtension(modelBuilder, builder.Build());
    }

    /// <summary>Adds or replaces a canonical PostgreSQL extension definition.</summary>
    public static ModelBuilder HasExtension(
        this ModelBuilder modelBuilder,
        BlueTuskExtensionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        BlueTuskExtensionMetadata.Validate(definition);
        definition = BlueTuskExtensionMetadata.Normalize(definition);
        var definitions = BlueTuskExtensionMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskExtensionDefinitionSet(
            definitions.Extensions
                .Where(item => !string.Equals(item.Name, definition.Name, StringComparison.Ordinal))
                .Append(definition)
                .ToArray()));
    }

    /// <summary>Removes a PostgreSQL extension installation from the model.</summary>
    public static ModelBuilder HasNoExtension(this ModelBuilder modelBuilder, string name)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var definitions = BlueTuskExtensionMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskExtensionDefinitionSet(
            definitions.Extensions
                .Where(item => !string.Equals(item.Name, name, StringComparison.Ordinal))
                .ToArray()));
    }

    /// <summary>Reads provider-owned PostgreSQL extension definitions from an EF model.</summary>
    public static BlueTuskExtensionDefinitionSet GetExtensions(this IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BlueTuskExtensionMetadata.Get(model);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasExtensions(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return Set(modelBuilder, BlueTuskExtensionMetadata.Deserialize(serializedDefinitions));
    }

    private static ModelBuilder Set(ModelBuilder modelBuilder, BlueTuskExtensionDefinitionSet definitions)
    {
        if (definitions.Extensions.Count == 0)
        {
            modelBuilder.Model.RemoveAnnotation(BlueTuskExtensionMetadata.AnnotationName);
        }
        else
        {
            modelBuilder.Model.SetAnnotation(
                BlueTuskExtensionMetadata.AnnotationName,
                BlueTuskExtensionMetadata.Serialize(definitions));
        }

        return modelBuilder;
    }
}

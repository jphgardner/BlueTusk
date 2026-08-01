using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Tablespaces;
using BlueTusk.EntityFrameworkCore.Tablespaces.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures provider-owned PostgreSQL tablespaces.</summary>
public static class BlueTuskTablespaceModelBuilderExtensions
{
    /// <summary>Adds or replaces a cluster-wide PostgreSQL tablespace.</summary>
    public static ModelBuilder HasBlueTuskTablespace(
        this ModelBuilder modelBuilder,
        string name,
        string location,
        Action<BlueTuskTablespaceBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var builder = new BlueTuskTablespaceBuilder(name, location);
        configure?.Invoke(builder);
        return HasBlueTuskTablespace(modelBuilder, builder.Build());
    }

    /// <summary>Adds or replaces a canonical tablespace definition.</summary>
    public static ModelBuilder HasBlueTuskTablespace(
        this ModelBuilder modelBuilder,
        BlueTuskTablespaceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        BlueTuskTablespaceMetadata.Validate(definition);
        definition = BlueTuskTablespaceMetadata.Normalize(definition);
        var definitions = BlueTuskTablespaceMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskTablespaceDefinitionSet(
            definitions.Tablespaces.Where(item => item.Name != definition.Name)
                .Append(definition)
                .ToArray()));
    }

    /// <summary>Removes a provider-owned tablespace from the model.</summary>
    public static ModelBuilder HasNoBlueTuskTablespace(this ModelBuilder modelBuilder, string name)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var definitions = BlueTuskTablespaceMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskTablespaceDefinitionSet(
            definitions.Tablespaces.Where(item => item.Name != name).ToArray()));
    }

    /// <summary>Reads all provider-owned tablespaces from the model.</summary>
    public static BlueTuskTablespaceDefinitionSet GetBlueTuskTablespaces(this IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BlueTuskTablespaceMetadata.Get(model);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasBlueTuskTablespaces(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return Set(modelBuilder, BlueTuskTablespaceMetadata.Deserialize(serializedDefinitions));
    }

    private static ModelBuilder Set(ModelBuilder modelBuilder, BlueTuskTablespaceDefinitionSet definitions)
    {
        BlueTuskTablespaceMetadata.Validate(definitions);
        if (definitions.Tablespaces.Count == 0)
        {
            modelBuilder.Model.RemoveAnnotation(BlueTuskTablespaceMetadata.AnnotationName);
        }
        else
        {
            modelBuilder.Model.SetAnnotation(
                BlueTuskTablespaceMetadata.AnnotationName,
                BlueTuskTablespaceMetadata.Serialize(definitions));
        }

        return modelBuilder;
    }
}

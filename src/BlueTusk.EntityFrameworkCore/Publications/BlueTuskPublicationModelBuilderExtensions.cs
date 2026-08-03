using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Publications;
using BlueTusk.EntityFrameworkCore.Publications.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Microsoft.EntityFrameworkCore;

public static class BlueTuskPublicationModelBuilderExtensions
{
    public static ModelBuilder HasBlueTuskPublication(
        this ModelBuilder modelBuilder,
        string name,
        Action<BlueTuskPublicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var builder = new BlueTuskPublicationBuilder(name);
        configure?.Invoke(builder);
        return HasBlueTuskPublication(modelBuilder, builder.Build());
    }

    public static ModelBuilder HasBlueTuskPublication(
        this ModelBuilder modelBuilder,
        BlueTuskPublicationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        BlueTuskPublicationMetadata.Validate(definition);
        definition = BlueTuskPublicationMetadata.Normalize(definition);
        var definitions = BlueTuskPublicationMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskPublicationDefinitionSet(
            definitions.Publications
                .Where(item => !string.Equals(item.Name, definition.Name, StringComparison.Ordinal))
                .Append(definition)
                .ToArray()));
    }

    public static ModelBuilder HasNoBlueTuskPublication(this ModelBuilder modelBuilder, string name)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var definitions = BlueTuskPublicationMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskPublicationDefinitionSet(
            definitions.Publications
                .Where(item => !string.Equals(item.Name, name, StringComparison.Ordinal))
                .ToArray()));
    }

    public static BlueTuskPublicationDefinitionSet GetBlueTuskPublications(this IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BlueTuskPublicationMetadata.Get(model);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasBlueTuskPublications(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return Set(modelBuilder, BlueTuskPublicationMetadata.Deserialize(serializedDefinitions));
    }

    private static ModelBuilder Set(ModelBuilder modelBuilder, BlueTuskPublicationDefinitionSet definitions)
    {
        if (definitions.Publications.Count == 0)
        {
            modelBuilder.Model.RemoveAnnotation(BlueTuskPublicationMetadata.AnnotationName);
        }
        else
        {
            modelBuilder.Model.SetAnnotation(
                BlueTuskPublicationMetadata.AnnotationName,
                BlueTuskPublicationMetadata.Serialize(definitions));
        }

        return modelBuilder;
    }
}

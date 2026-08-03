using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Collations;
using BlueTusk.EntityFrameworkCore.Collations.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Model-builder extensions for provider-owned PostgreSQL collations.</summary>
public static class BlueTuskCollationModelBuilderExtensions
{
    /// <summary>Adds or replaces a PostgreSQL collation in the model.</summary>
    public static ModelBuilder HasCollation(
        this ModelBuilder modelBuilder,
        string name,
        Action<BlueTuskCollationBuilder> buildAction,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(buildAction);
        var builder = new BlueTuskCollationBuilder(name, schema);
        buildAction(builder);
        return HasCollation(modelBuilder, builder.Build());
    }

    /// <summary>Adds or replaces a canonical PostgreSQL collation definition.</summary>
    public static ModelBuilder HasCollation(
        this ModelBuilder modelBuilder,
        BlueTuskCollationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        BlueTuskCollationMetadata.Validate(definition);
        var definitions = BlueTuskCollationMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskCollationDefinitionSet(
            definitions.Collations
                .Where(item => !string.Equals(item.Name, definition.Name, StringComparison.Ordinal) ||
                               !string.Equals(item.Schema, definition.Schema, StringComparison.Ordinal))
                .Append(definition)
                .ToArray()));
    }

    /// <summary>Removes a PostgreSQL collation from the model.</summary>
    public static ModelBuilder HasNoCollation(
        this ModelBuilder modelBuilder,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var definitions = BlueTuskCollationMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskCollationDefinitionSet(
            definitions.Collations
                .Where(item => !string.Equals(item.Name, name, StringComparison.Ordinal) ||
                               !string.Equals(item.Schema, schema, StringComparison.Ordinal))
                .ToArray()));
    }

    /// <summary>Reads provider-owned PostgreSQL collation definitions from an EF model.</summary>
    public static BlueTuskCollationDefinitionSet GetCollations(this IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BlueTuskCollationMetadata.Get(model);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasCollations(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return Set(modelBuilder, BlueTuskCollationMetadata.Deserialize(serializedDefinitions));
    }

    private static ModelBuilder Set(ModelBuilder modelBuilder, BlueTuskCollationDefinitionSet definitions)
    {
        if (definitions.Collations.Count == 0)
        {
            modelBuilder.Model.RemoveAnnotation(BlueTuskCollationMetadata.AnnotationName);
        }
        else
        {
            modelBuilder.Model.SetAnnotation(
                BlueTuskCollationMetadata.AnnotationName,
                BlueTuskCollationMetadata.Serialize(definitions));
        }

        return modelBuilder;
    }
}

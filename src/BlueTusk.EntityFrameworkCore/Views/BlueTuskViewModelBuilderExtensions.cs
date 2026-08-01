using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Views;
using BlueTusk.EntityFrameworkCore.Views.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Model-builder extensions for provider-owned PostgreSQL views.</summary>
public static class BlueTuskViewModelBuilderExtensions
{
    /// <summary>Adds or replaces a model-authored ordinary PostgreSQL view.</summary>
    public static ModelBuilder HasBlueTuskView(
        this ModelBuilder modelBuilder,
        string name,
        string querySql,
        Action<BlueTuskViewBuilder>? buildAction = null,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var builder = new BlueTuskViewBuilder(name, schema, querySql);
        buildAction?.Invoke(builder);
        return HasBlueTuskView(modelBuilder, builder.Build());
    }

    /// <summary>Adds or replaces a canonical provider-owned ordinary PostgreSQL view.</summary>
    public static ModelBuilder HasBlueTuskView(
        this ModelBuilder modelBuilder,
        BlueTuskViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        BlueTuskViewMetadata.Validate(definition);
        definition = BlueTuskViewMetadata.Normalize(definition);
        var definitions = BlueTuskViewMetadata.Get(modelBuilder.Model);
        var key = BlueTuskViewMetadata.ViewKey.Create(definition);
        return Set(modelBuilder, definitions with
        {
            Views = definitions.Views
                .Where(item => BlueTuskViewMetadata.ViewKey.Create(item) != key)
                .Append(definition)
                .ToArray(),
            MaterializedViews = definitions.MaterializedViews
                .Where(item => BlueTuskViewMetadata.ViewKey.Create(item) != key)
                .ToArray(),
        });
    }

    /// <summary>Adds or replaces a model-authored PostgreSQL materialized view.</summary>
    public static ModelBuilder HasBlueTuskMaterializedView(
        this ModelBuilder modelBuilder,
        string name,
        string querySql,
        Action<BlueTuskMaterializedViewBuilder>? buildAction = null,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var builder = new BlueTuskMaterializedViewBuilder(name, schema, querySql);
        buildAction?.Invoke(builder);
        return HasBlueTuskMaterializedView(modelBuilder, builder.Build());
    }

    /// <summary>Adds or replaces a canonical provider-owned PostgreSQL materialized view.</summary>
    public static ModelBuilder HasBlueTuskMaterializedView(
        this ModelBuilder modelBuilder,
        BlueTuskMaterializedViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        BlueTuskViewMetadata.Validate(definition);
        definition = BlueTuskViewMetadata.Normalize(definition);
        var definitions = BlueTuskViewMetadata.Get(modelBuilder.Model);
        var key = BlueTuskViewMetadata.ViewKey.Create(definition);
        return Set(modelBuilder, definitions with
        {
            Views = definitions.Views
                .Where(item => BlueTuskViewMetadata.ViewKey.Create(item) != key)
                .ToArray(),
            MaterializedViews = definitions.MaterializedViews
                .Where(item => BlueTuskViewMetadata.ViewKey.Create(item) != key)
                .Append(definition)
                .ToArray(),
        });
    }

    /// <summary>Removes an ordinary or materialized view from provider-owned metadata.</summary>
    public static ModelBuilder HasNoBlueTuskView(
        this ModelBuilder modelBuilder,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var key = new BlueTuskViewMetadata.ViewKey(schema, name);
        var definitions = BlueTuskViewMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskViewDefinitionSet(
            definitions.Views.Where(item => BlueTuskViewMetadata.ViewKey.Create(item) != key).ToArray(),
            definitions.MaterializedViews.Where(item => BlueTuskViewMetadata.ViewKey.Create(item) != key).ToArray()));
    }

    /// <summary>Reads all provider-owned PostgreSQL view definitions from an EF model.</summary>
    public static BlueTuskViewDefinitionSet GetBlueTuskViews(this IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BlueTuskViewMetadata.Get(model);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasBlueTuskViews(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return Set(modelBuilder, BlueTuskViewMetadata.Deserialize(serializedDefinitions));
    }

    private static ModelBuilder Set(ModelBuilder modelBuilder, BlueTuskViewDefinitionSet definitions)
    {
        if (definitions.Views.Count == 0 && definitions.MaterializedViews.Count == 0)
        {
            modelBuilder.Model.RemoveAnnotation(BlueTuskViewMetadata.AnnotationName);
        }
        else
        {
            modelBuilder.Model.SetAnnotation(
                BlueTuskViewMetadata.AnnotationName,
                BlueTuskViewMetadata.Serialize(definitions));
        }

        return modelBuilder;
    }
}

using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Graphs.Internal;

namespace Microsoft.EntityFrameworkCore;

/// <summary>PostgreSQL property-graph extensions for EF models.</summary>
public static class BlueTuskPropertyGraphModelBuilderExtensions
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasBlueTuskPropertyGraphs(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var definitions = BlueTuskPropertyGraphMetadata.DeserializeMany(serializedDefinitions);
        modelBuilder.Model.SetAnnotation(
            BlueTuskPropertyGraphMetadata.AnnotationName,
            BlueTuskPropertyGraphMetadata.Serialize(definitions));
        return modelBuilder;
    }

    public static ModelBuilder HasBlueTuskPropertyGraph(
        this ModelBuilder modelBuilder,
        string name,
        Action<BlueTuskPropertyGraphBuilder> configure,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        schema ??= modelBuilder.Model.GetDefaultSchema();
        var builder = new BlueTuskPropertyGraphBuilder(modelBuilder, name, schema);
        configure(builder);
        var definition = builder.Build();

        var graphs = BlueTuskPropertyGraphMetadata.Get(modelBuilder.Model)
            .Where(graph =>
                !string.Equals(graph.Name, name, StringComparison.Ordinal) ||
                !string.Equals(graph.Schema, schema, StringComparison.Ordinal))
            .Append(definition)
            .ToArray();
        modelBuilder.Model.SetAnnotation(
            BlueTuskPropertyGraphMetadata.AnnotationName,
            BlueTuskPropertyGraphMetadata.Serialize(graphs));
        return modelBuilder;
    }
}

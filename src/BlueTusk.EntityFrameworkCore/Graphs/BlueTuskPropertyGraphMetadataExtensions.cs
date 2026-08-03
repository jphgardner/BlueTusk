using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Graphs;

/// <summary>Reads BlueTusk property-graph definitions from EF model metadata.</summary>
public static class BlueTuskPropertyGraphMetadataExtensions
{
    public static IReadOnlyList<BlueTuskPropertyGraphDefinition> GetPropertyGraphs(
        this IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BlueTuskPropertyGraphMetadata.Get(model);
    }
}

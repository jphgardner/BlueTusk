using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Graphs.Internal;

internal static class BlueTuskPropertyGraphMetadata
{
    public const string AnnotationName = "BlueTusk:PropertyGraphs";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly ConditionalWeakTable<IModel, IReadOnlyList<BlueTuskPropertyGraphDefinition>> RuntimeGraphs = new();

    internal static IReadOnlyList<BlueTuskPropertyGraphDefinition> GetRuntimeGraphs(IModel model) =>
        RuntimeGraphs.GetValue(model, static key => Get(key));

    public static IReadOnlyList<BlueTuskPropertyGraphDefinition> Get(IReadOnlyAnnotatable annotatable)
    {
        BlueTuskGuard.NotNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        if (string.IsNullOrEmpty(json))
        {
            return Array.Empty<BlueTuskPropertyGraphDefinition>();
        }

        return JsonSerializer.Deserialize<BlueTuskPropertyGraphDefinition[]>(json, SerializerOptions)
            ?? Array.Empty<BlueTuskPropertyGraphDefinition>();
    }

    public static string Serialize(IEnumerable<BlueTuskPropertyGraphDefinition> graphs)
    {
        BlueTuskGuard.NotNull(graphs);
        return JsonSerializer.Serialize(
            graphs
                .OrderBy(graph => graph.Schema, StringComparer.Ordinal)
                .ThenBy(graph => graph.Name, StringComparer.Ordinal)
                .ToArray(),
            SerializerOptions);
    }

    public static string Serialize(BlueTuskPropertyGraphDefinition graph)
    {
        BlueTuskGuard.NotNull(graph);
        return JsonSerializer.Serialize(graph, SerializerOptions);
    }

    public static BlueTuskPropertyGraphDefinition Deserialize(string json)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<BlueTuskPropertyGraphDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The property-graph definition is empty.", nameof(json));
    }

    public static IReadOnlyList<BlueTuskPropertyGraphDefinition> DeserializeMany(string json)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<BlueTuskPropertyGraphDefinition[]>(json, SerializerOptions)
            ?? throw new ArgumentException("The property-graph definitions are empty.", nameof(json));
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.EventTriggers.Internal;

internal static class BlueTuskEventTriggerMetadata
{
    public const string AnnotationName = "BlueTusk:EventTriggers";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static BlueTuskEventTriggerDefinitionSet Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var value = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(value) ? BlueTuskEventTriggerDefinitionSet.Empty : Deserialize(value);
    }

    public static string Serialize(BlueTuskEventTriggerDefinitionSet definitions)
    {
        Validate(definitions);
        return JsonSerializer.Serialize(Normalize(definitions), SerializerOptions);
    }

    public static BlueTuskEventTriggerDefinitionSet Deserialize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var definitions = JsonSerializer.Deserialize<BlueTuskEventTriggerDefinitionSet>(value, SerializerOptions)
            ?? throw new ArgumentException("The event-trigger definition set is empty.", nameof(value));
        Validate(definitions);
        return Normalize(definitions);
    }

    public static string Serialize(BlueTuskEventTriggerDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static BlueTuskEventTriggerDefinition DeserializeDefinition(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var definition = JsonSerializer.Deserialize<BlueTuskEventTriggerDefinition>(value, SerializerOptions)
            ?? throw new ArgumentException("The event-trigger definition is empty.", nameof(value));
        Validate(definition);
        return Normalize(definition);
    }

    public static BlueTuskEventTriggerDefinitionSet Normalize(BlueTuskEventTriggerDefinitionSet definitions) => new(
        definitions.EventTriggers.Select(Normalize)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray());

    public static BlueTuskEventTriggerDefinition Normalize(BlueTuskEventTriggerDefinition definition) =>
        definition with
        {
            Tags = definition.Tags.OrderBy(tag => tag, StringComparer.Ordinal).ToArray(),
        };

    public static void Validate(BlueTuskEventTriggerDefinitionSet definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(definitions.EventTriggers);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions.EventTriggers)
        {
            Validate(definition);
            if (!names.Add(definition.Name))
            {
                throw new ArgumentException($"Event trigger '{definition.Name}' is configured more than once.");
            }
        }
    }

    public static void Validate(BlueTuskEventTriggerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ArgumentNullException.ThrowIfNull(definition.Function);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Function.Name);
        if (definition.Function.Schema is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Function.Schema);
        }

        if (!Enum.IsDefined(definition.Event) || !Enum.IsDefined(definition.EnabledMode))
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "The event or enabled mode is invalid.");
        }

        ArgumentNullException.ThrowIfNull(definition.Tags);
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in definition.Tags)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);
            if (!tags.Add(tag))
            {
                throw new ArgumentException($"Event-trigger command tag '{tag}' is configured more than once.");
            }
        }

        if (definition.Event == BlueTuskEventTriggerEvent.Login && definition.Tags.Count > 0)
        {
            throw new ArgumentException("PostgreSQL login event triggers cannot filter by command tag.",
                nameof(definition));
        }
    }

    public static bool CreateBodyEquals(
        BlueTuskEventTriggerDefinition left,
        BlueTuskEventTriggerDefinition right) =>
        left.Event == right.Event &&
        left.Function == right.Function &&
        left.Tags.SequenceEqual(right.Tags, StringComparer.Ordinal);
}

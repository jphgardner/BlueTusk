using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.Tablespaces.Internal;

internal static class BlueTuskTablespaceMetadata
{
    public const string AnnotationName = "BlueTusk:Tablespaces";

    private static readonly HashSet<string> SupportedOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "seq_page_cost",
        "random_page_cost",
        "effective_io_concurrency",
        "maintenance_io_concurrency",
    };

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static BlueTuskTablespaceDefinitionSet Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var value = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(value) ? BlueTuskTablespaceDefinitionSet.Empty : Deserialize(value);
    }

    public static string Serialize(BlueTuskTablespaceDefinitionSet definitions)
    {
        Validate(definitions);
        return JsonSerializer.Serialize(Normalize(definitions), SerializerOptions);
    }

    public static BlueTuskTablespaceDefinitionSet Deserialize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var definitions = JsonSerializer.Deserialize<BlueTuskTablespaceDefinitionSet>(value, SerializerOptions)
            ?? throw new ArgumentException("The tablespace definition set is empty.", nameof(value));
        Validate(definitions);
        return Normalize(definitions);
    }

    public static string Serialize(BlueTuskTablespaceDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static BlueTuskTablespaceDefinition DeserializeDefinition(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var definition = JsonSerializer.Deserialize<BlueTuskTablespaceDefinition>(value, SerializerOptions)
            ?? throw new ArgumentException("The tablespace definition is empty.", nameof(value));
        Validate(definition);
        return Normalize(definition);
    }

    public static BlueTuskTablespaceDefinitionSet Normalize(BlueTuskTablespaceDefinitionSet definitions) => new(
        definitions.Tablespaces.Select(Normalize)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray());

    public static BlueTuskTablespaceDefinition Normalize(BlueTuskTablespaceDefinition definition) =>
        definition with
        {
            Options = definition.Options
                .Select(option => option with { Name = option.Name.ToLowerInvariant() })
                .OrderBy(option => option.Name, StringComparer.Ordinal)
                .ToArray(),
        };

    public static void Validate(BlueTuskTablespaceDefinitionSet definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(definitions.Tablespaces);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions.Tablespaces)
        {
            Validate(definition);
            if (!names.Add(definition.Name))
            {
                throw new ArgumentException($"Tablespace '{definition.Name}' is configured more than once.");
            }
        }
    }

    public static void Validate(BlueTuskTablespaceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Location);
        if (definition.Name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("PostgreSQL reserves tablespace names beginning with 'pg_'.",
                nameof(definition));
        }

        if (definition.Owner is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Owner);
        }

        ArgumentNullException.ThrowIfNull(definition.Options);
        var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in definition.Options)
        {
            ArgumentNullException.ThrowIfNull(option);
            ArgumentException.ThrowIfNullOrWhiteSpace(option.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(option.Value);
            if (!SupportedOptions.Contains(option.Name))
            {
                throw new ArgumentException($"Unsupported PostgreSQL tablespace option '{option.Name}'.",
                    nameof(definition));
            }

            if (!options.Add(option.Name))
            {
                throw new ArgumentException($"Tablespace option '{option.Name}' is configured more than once.");
            }
        }
    }

    public static bool LocationEquals(
        BlueTuskTablespaceDefinition left,
        BlueTuskTablespaceDefinition right) =>
        string.Equals(left.Location, right.Location, StringComparison.Ordinal);
}

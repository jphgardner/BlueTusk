using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.Extensions.Internal;

internal static class BlueTuskExtensionMetadata
{
    public const string AnnotationName = "BlueTusk:Extensions";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static BlueTuskExtensionDefinitionSet Get(IReadOnlyAnnotatable annotatable)
    {
        BlueTuskGuard.NotNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? BlueTuskExtensionDefinitionSet.Empty : Deserialize(json);
    }

    public static string Serialize(BlueTuskExtensionDefinitionSet definitions)
    {
        Validate(definitions);
        return JsonSerializer.Serialize(Normalize(definitions), SerializerOptions);
    }

    public static string Serialize(BlueTuskExtensionDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static BlueTuskExtensionDefinitionSet Deserialize(string json)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskExtensionDefinitionSet>(json, SerializerOptions)
            ?? throw new ArgumentException("The extension definition set is empty.", nameof(json));
        Validate(definitions);
        return Normalize(definitions);
    }

    public static BlueTuskExtensionDefinition DeserializeDefinition(string json)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskExtensionDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The extension definition is empty.", nameof(json));
        Validate(definition);
        return Normalize(definition);
    }

    public static void Validate(BlueTuskExtensionDefinitionSet definitions)
    {
        BlueTuskGuard.NotNull(definitions);
        BlueTuskGuard.NotNull(definitions.Extensions);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions.Extensions)
        {
            Validate(definition);
            if (!names.Add(definition.Name))
            {
                throw new ArgumentException(
                    $"PostgreSQL extension '{definition.Name}' is configured more than once.",
                    nameof(definitions));
            }
        }

        ValidateDependencyCycles(definitions.Extensions);
    }

    public static void Validate(BlueTuskExtensionDefinition definition)
    {
        BlueTuskGuard.NotNull(definition);
        BlueTuskGuard.NotNullOrWhiteSpace(definition.Name);
        if (definition.Schema is not null)
        {
            BlueTuskGuard.NotNullOrWhiteSpace(definition.Schema);
        }

        if (definition.Version is not null)
        {
            BlueTuskGuard.NotNullOrWhiteSpace(definition.Version);
        }

        BlueTuskGuard.NotNull(definition.Dependencies);
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in definition.Dependencies)
        {
            BlueTuskGuard.NotNullOrWhiteSpace(dependency);
            if (string.Equals(dependency, definition.Name, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"PostgreSQL extension '{definition.Name}' cannot depend on itself.",
                    nameof(definition));
            }

            if (!dependencies.Add(dependency))
            {
                throw new ArgumentException(
                    $"PostgreSQL extension '{definition.Name}' contains duplicate dependency '{dependency}'.",
                    nameof(definition));
            }
        }
    }

    public static BlueTuskExtensionDefinitionSet Normalize(BlueTuskExtensionDefinitionSet definitions) =>
        new(definitions.Extensions.Select(Normalize)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray());

    public static BlueTuskExtensionDefinition Normalize(BlueTuskExtensionDefinition definition) => definition with
    {
        Dependencies = definition.Dependencies.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
    };

    private static void ValidateDependencyCycles(IReadOnlyList<BlueTuskExtensionDefinition> definitions)
    {
        var byName = definitions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            Visit(definition);
        }

        return;

        void Visit(BlueTuskExtensionDefinition definition)
        {
            if (visited.Contains(definition.Name))
            {
                return;
            }

            if (!visiting.Add(definition.Name))
            {
                throw new ArgumentException(
                    $"PostgreSQL extension dependency metadata contains a cycle through '{definition.Name}'.",
                    nameof(definitions));
            }

            foreach (var dependency in definition.Dependencies)
            {
                if (byName.TryGetValue(dependency, out var dependencyDefinition))
                {
                    Visit(dependencyDefinition);
                }
            }

            visiting.Remove(definition.Name);
            visited.Add(definition.Name);
        }
    }
}

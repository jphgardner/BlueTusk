using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.Collations.Internal;

internal static class BlueTuskCollationMetadata
{
    public const string AnnotationName = "BlueTusk:Collations";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static BlueTuskCollationDefinitionSet Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? BlueTuskCollationDefinitionSet.Empty : Deserialize(json);
    }

    public static string Serialize(BlueTuskCollationDefinitionSet definitions)
    {
        Validate(definitions);
        return JsonSerializer.Serialize(Normalize(definitions), SerializerOptions);
    }

    public static string Serialize(BlueTuskCollationDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(definition, SerializerOptions);
    }

    public static BlueTuskCollationDefinitionSet Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskCollationDefinitionSet>(json, SerializerOptions)
            ?? throw new ArgumentException("The collation definition set is empty.", nameof(json));
        Validate(definitions);
        return Normalize(definitions);
    }

    public static BlueTuskCollationDefinition DeserializeDefinition(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskCollationDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The collation definition is empty.", nameof(json));
        Validate(definition);
        return definition;
    }

    public static void Validate(BlueTuskCollationDefinitionSet definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(definitions.Collations);
        var names = new HashSet<(string? Schema, string Name)>();
        foreach (var definition in definitions.Collations)
        {
            Validate(definition);
            if (!names.Add((definition.Schema, definition.Name)))
            {
                throw new ArgumentException(
                    $"PostgreSQL collation '{definition.Schema}.{definition.Name}' is configured more than once.",
                    nameof(definitions));
            }
        }
    }

    public static void Validate(BlueTuskCollationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ValidateOptional(definition.Schema, nameof(definition.Schema));
        ValidateOptional(definition.Locale, nameof(definition.Locale));
        ValidateOptional(definition.LcCollate, nameof(definition.LcCollate));
        ValidateOptional(definition.LcCtype, nameof(definition.LcCtype));
        ValidateOptional(definition.Rules, nameof(definition.Rules));
        ValidateOptional(definition.Version, nameof(definition.Version));
        if (definition.Provider is { } configuredProvider && !Enum.IsDefined(configuredProvider))
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                configuredProvider,
                "Unknown PostgreSQL collation provider.");
        }

        if (definition.Locale is not null &&
            (definition.LcCollate is not null || definition.LcCtype is not null))
        {
            throw new ArgumentException(
                $"PostgreSQL collation '{definition.Schema}.{definition.Name}' cannot combine LOCALE with LC_COLLATE or LC_CTYPE.",
                nameof(definition));
        }

        if (definition.Locale is null &&
            (definition.LcCollate is null || definition.LcCtype is null))
        {
            throw new ArgumentException(
                $"PostgreSQL collation '{definition.Schema}.{definition.Name}' must configure LOCALE or both LC_COLLATE and LC_CTYPE.",
                nameof(definition));
        }

        var provider = definition.Provider ?? BlueTuskCollationProvider.Libc;
        if (provider != BlueTuskCollationProvider.Libc &&
            (definition.LcCollate is not null || definition.LcCtype is not null))
        {
            throw new ArgumentException(
                $"PostgreSQL collation provider '{provider}' does not accept LC_COLLATE or LC_CTYPE.",
                nameof(definition));
        }

        if (provider != BlueTuskCollationProvider.Icu && definition.Rules is not null)
        {
            throw new ArgumentException("PostgreSQL collation rules require the ICU provider.", nameof(definition));
        }

        if (provider != BlueTuskCollationProvider.Icu && definition.IsDeterministic == false)
        {
            throw new ArgumentException("Nondeterministic PostgreSQL collations require the ICU provider.", nameof(definition));
        }
    }

    public static BlueTuskCollationDefinitionSet Normalize(BlueTuskCollationDefinitionSet definitions) =>
        new(definitions.Collations
            .OrderBy(definition => definition.Schema, StringComparer.Ordinal)
            .ThenBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray());

    private static void ValidateOptional(string? value, string parameterName)
    {
        if (value is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        }
    }
}

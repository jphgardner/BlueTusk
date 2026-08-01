using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.UserDefinedTypes.Internal;

internal static class BlueTuskUserDefinedTypeMetadata
{
    public const string AnnotationName = "BlueTusk:UserDefinedTypes";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static BlueTuskUserDefinedTypeDefinitionSet Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json)
            ? BlueTuskUserDefinedTypeDefinitionSet.Empty
            : Deserialize(json);
    }

    public static string Serialize(BlueTuskUserDefinedTypeDefinitionSet definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        Validate(definitions);
        var normalized = Normalize(definitions);
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static string Serialize(BlueTuskEnumTypeDefinition definition) =>
        JsonSerializer.Serialize(ValidateAndReturn(definition), SerializerOptions);

    public static string Serialize(BlueTuskDomainTypeDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static string Serialize(BlueTuskCompositeTypeDefinition definition) =>
        JsonSerializer.Serialize(ValidateAndReturn(definition), SerializerOptions);

    public static string Serialize(BlueTuskRangeTypeDefinition definition) =>
        JsonSerializer.Serialize(ValidateAndReturn(definition), SerializerOptions);

    public static BlueTuskUserDefinedTypeDefinitionSet Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskUserDefinedTypeDefinitionSet>(json, SerializerOptions)
            ?? throw new ArgumentException("The user-defined type definition set is empty.", nameof(json));
        Validate(definitions);
        return Normalize(definitions);
    }

    public static BlueTuskEnumTypeDefinition DeserializeEnum(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return ValidateAndReturn(
            JsonSerializer.Deserialize<BlueTuskEnumTypeDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The enum definition is empty.", nameof(json)));
    }

    public static BlueTuskDomainTypeDefinition DeserializeDomain(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskDomainTypeDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The domain definition is empty.", nameof(json));
        Validate(definition);
        return Normalize(definition);
    }

    public static BlueTuskCompositeTypeDefinition DeserializeComposite(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return ValidateAndReturn(
            JsonSerializer.Deserialize<BlueTuskCompositeTypeDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The composite definition is empty.", nameof(json)));
    }

    public static BlueTuskRangeTypeDefinition DeserializeRange(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return ValidateAndReturn(
            JsonSerializer.Deserialize<BlueTuskRangeTypeDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The range definition is empty.", nameof(json)));
    }

    public static void Validate(BlueTuskUserDefinedTypeDefinitionSet definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(definitions.Enums);
        ArgumentNullException.ThrowIfNull(definitions.Domains);
        ArgumentNullException.ThrowIfNull(definitions.Composites);
        ArgumentNullException.ThrowIfNull(definitions.Ranges);
        var names = new HashSet<(string? Schema, string Name)>();
        foreach (var definition in definitions.Enums)
        {
            Validate(definition);
            AddName(definition.Name, definition.Schema, names);
        }

        foreach (var definition in definitions.Domains)
        {
            Validate(definition);
            AddName(definition.Name, definition.Schema, names);
        }

        foreach (var definition in definitions.Composites)
        {
            Validate(definition);
            AddName(definition.Name, definition.Schema, names);
        }

        foreach (var definition in definitions.Ranges)
        {
            Validate(definition);
            AddName(definition.Name, definition.Schema, names);
            AddName(definition.MultirangeType.Name, definition.MultirangeType.Schema, names);
        }
    }

    public static void Validate(BlueTuskEnumTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateName(definition.Name, definition.Schema);
        ArgumentNullException.ThrowIfNull(definition.Labels);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in definition.Labels)
        {
            ArgumentNullException.ThrowIfNull(label);
            if (!labels.Add(label))
            {
                throw new ArgumentException(
                    $"Enum type '{definition.Schema}.{definition.Name}' contains duplicate label '{label}'.",
                    nameof(definition));
            }
        }
    }

    public static void Validate(BlueTuskDomainTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateName(definition.Name, definition.Schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.BaseStoreType);
        ValidateOptionalSql(definition.Collation, nameof(definition.Collation));
        ValidateOptionalSql(definition.DefaultSql, nameof(definition.DefaultSql));
        ArgumentNullException.ThrowIfNull(definition.Constraints);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var constraint in definition.Constraints)
        {
            ArgumentNullException.ThrowIfNull(constraint);
            ArgumentException.ThrowIfNullOrWhiteSpace(constraint.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(constraint.CheckSql);
            if (!names.Add(constraint.Name))
            {
                throw new ArgumentException(
                    $"Domain '{definition.Schema}.{definition.Name}' contains duplicate constraint '{constraint.Name}'.",
                    nameof(definition));
            }
        }
    }

    public static void Validate(BlueTuskCompositeTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateName(definition.Name, definition.Schema);
        ArgumentNullException.ThrowIfNull(definition.Attributes);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in definition.Attributes)
        {
            ArgumentNullException.ThrowIfNull(attribute);
            ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(attribute.StoreType);
            ValidateOptionalSql(attribute.Collation, nameof(attribute.Collation));
            if (!names.Add(attribute.Name))
            {
                throw new ArgumentException(
                    $"Composite type '{definition.Schema}.{definition.Name}' contains duplicate attribute '{attribute.Name}'.",
                    nameof(definition));
            }
        }
    }

    public static void Validate(BlueTuskRangeTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateName(definition.Name, definition.Schema);
        Validate(definition.Subtype, nameof(definition.Subtype));
        Validate(definition.SubtypeOperatorClass, nameof(definition.SubtypeOperatorClass));
        Validate(definition.Collation, nameof(definition.Collation));
        Validate(definition.CanonicalFunction, nameof(definition.CanonicalFunction));
        Validate(definition.SubtypeDifferenceFunction, nameof(definition.SubtypeDifferenceFunction));
        Validate(definition.MultirangeType, nameof(definition.MultirangeType));
        if (string.Equals(definition.Name, definition.MultirangeType.Name, StringComparison.Ordinal) &&
            string.Equals(definition.Schema, definition.MultirangeType.Schema, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Range type '{definition.Schema}.{definition.Name}' cannot use the same name for its multirange type.",
                nameof(definition));
        }
    }

    private static BlueTuskUserDefinedTypeDefinitionSet Normalize(
        BlueTuskUserDefinedTypeDefinitionSet definitions) =>
        new(
            definitions.Enums.OrderBy(definition => definition.Schema, StringComparer.Ordinal)
                .ThenBy(definition => definition.Name, StringComparer.Ordinal)
                .ToArray(),
            definitions.Domains.Select(Normalize)
                .OrderBy(definition => definition.Schema, StringComparer.Ordinal)
                .ThenBy(definition => definition.Name, StringComparer.Ordinal)
                .ToArray(),
            definitions.Composites.OrderBy(definition => definition.Schema, StringComparer.Ordinal)
                .ThenBy(definition => definition.Name, StringComparer.Ordinal)
                .ToArray())
        {
            Ranges = definitions.Ranges.OrderBy(definition => definition.Schema, StringComparer.Ordinal)
                .ThenBy(definition => definition.Name, StringComparer.Ordinal)
                .ToArray(),
        };

    private static BlueTuskDomainTypeDefinition Normalize(BlueTuskDomainTypeDefinition definition) =>
        definition with
        {
            Constraints = definition.Constraints
                .OrderBy(constraint => constraint.Name, StringComparer.Ordinal)
                .ToArray(),
        };

    private static T ValidateAndReturn<T>(T definition)
        where T : class
    {
        switch (definition)
        {
            case BlueTuskEnumTypeDefinition enumDefinition:
                Validate(enumDefinition);
                break;
            case BlueTuskDomainTypeDefinition domainDefinition:
                Validate(domainDefinition);
                break;
            case BlueTuskCompositeTypeDefinition compositeDefinition:
                Validate(compositeDefinition);
                break;
            case BlueTuskRangeTypeDefinition rangeDefinition:
                Validate(rangeDefinition);
                break;
        }

        return definition;
    }

    private static void AddName(
        string name,
        string? schema,
        HashSet<(string? Schema, string Name)> names)
    {
        if (!names.Add((schema, name)))
        {
            throw new ArgumentException(
                $"PostgreSQL type name '{schema}.{name}' is configured more than once.");
        }
    }

    private static void ValidateName(string name, string? schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (schema is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        }
    }

    private static void Validate(BlueTuskQualifiedName? name, string parameterName)
    {
        if (name is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name.Name, parameterName);
        if (name.Schema is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name.Schema, parameterName);
        }
    }

    private static void ValidateOptionalSql(string? value, string parameterName)
    {
        if (value is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.ForeignData.Internal;

internal static partial class BlueTuskForeignDataMetadata
{
    public const string AnnotationName = "BlueTusk:ForeignData";
    public const string ForeignTableAnnotationName = "BlueTusk:ForeignTable";
    public const string ForeignColumnOptionsAnnotationName = "BlueTusk:ForeignColumnOptions";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static BlueTuskForeignDataDefinitionSet Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? BlueTuskForeignDataDefinitionSet.Empty : Deserialize(json);
    }

    public static string Serialize(BlueTuskForeignDataDefinitionSet definitions)
    {
        ValidateForModel(definitions);
        return JsonSerializer.Serialize(Normalize(definitions), SerializerOptions);
    }

    public static BlueTuskForeignDataDefinitionSet Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskForeignDataDefinitionSet>(json, SerializerOptions)
            ?? throw new ArgumentException("The foreign-data definition set is empty.", nameof(json));
        ValidateForModel(definitions);
        return Normalize(definitions);
    }

    public static string Serialize(BlueTuskForeignDataWrapperDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static BlueTuskForeignDataWrapperDefinition DeserializeWrapper(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskForeignDataWrapperDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The foreign-data wrapper definition is empty.", nameof(json));
        Validate(definition);
        return Normalize(definition);
    }

    public static string Serialize(BlueTuskForeignServerDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static BlueTuskForeignServerDefinition DeserializeServer(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskForeignServerDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The foreign-server definition is empty.", nameof(json));
        Validate(definition);
        return Normalize(definition);
    }

    public static string Serialize(BlueTuskUserMappingDefinition definition, bool forModel = true)
    {
        if (forModel)
        {
            ValidateForModel(definition);
        }
        else
        {
            Validate(definition);
        }

        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static BlueTuskUserMappingDefinition DeserializeUserMapping(string json, bool forModel = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskUserMappingDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The user-mapping definition is empty.", nameof(json));
        if (forModel)
        {
            ValidateForModel(definition);
        }
        else
        {
            Validate(definition);
        }

        return Normalize(definition);
    }

    public static string Serialize(BlueTuskForeignTableDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static BlueTuskForeignTableDefinition DeserializeForeignTable(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskForeignTableDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The foreign-table definition is empty.", nameof(json));
        Validate(definition);
        return Normalize(definition);
    }

    public static string SerializeOptions(IReadOnlyList<BlueTuskForeignOptionDefinition> options)
    {
        ValidateOptions(options);
        return JsonSerializer.Serialize(NormalizeOptions(options), SerializerOptions);
    }

    public static IReadOnlyList<BlueTuskForeignOptionDefinition> DeserializeOptions(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var options = JsonSerializer.Deserialize<BlueTuskForeignOptionDefinition[]>(json, SerializerOptions)
            ?? throw new ArgumentException("The foreign-column option list is empty.", nameof(json));
        ValidateOptions(options);
        return NormalizeOptions(options);
    }

    public static BlueTuskForeignTableDefinition? GetForeignTable(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(ForeignTableAnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? null : DeserializeForeignTable(json);
    }

    public static BlueTuskForeignTableDefinition? GetTableDefinition(ITable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var definitions = table.EntityTypeMappings
            .Select(mapping => mapping.TypeBase is IReadOnlyEntityType entityType
                ? GetForeignTable(entityType)
                : null)
            .Where(definition => definition is not null)
            .Cast<BlueTuskForeignTableDefinition>()
            .Distinct()
            .ToArray();
        if (definitions.Length > 1)
        {
            throw new InvalidOperationException(
                $"Table '{table.Schema}.{table.Name}' has inconsistent foreign-table metadata.");
        }

        var definition = definitions.SingleOrDefault();
        if (definition is not null)
        {
            var columns = table.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
            var unknown = definition.Columns.FirstOrDefault(column => !columns.Contains(column.Name));
            if (unknown is not null)
            {
                throw new InvalidOperationException(
                    $"Foreign-table option metadata references unmapped column '{unknown.Name}' on " +
                    $"'{table.Schema}.{table.Name}'.");
            }
        }

        return definition;
    }

    public static BlueTuskForeignDataDefinitionSet Normalize(BlueTuskForeignDataDefinitionSet definitions) => new(
        definitions.Wrappers.Select(Normalize).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
        definitions.Servers.Select(Normalize).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
        definitions.UserMappings.Select(Normalize)
            .OrderBy(item => item.ServerName, StringComparer.Ordinal)
            .ThenBy(item => item.UserName, StringComparer.Ordinal)
            .ToArray());

    public static BlueTuskForeignDataWrapperDefinition Normalize(BlueTuskForeignDataWrapperDefinition definition) =>
        definition with
        {
            HandlerFunction = NormalizeNullable(definition.HandlerFunction),
            ValidatorFunction = NormalizeNullable(definition.ValidatorFunction),
            ConnectionFunction = NormalizeNullable(definition.ConnectionFunction),
            Options = NormalizeOptions(definition.Options),
        };

    public static BlueTuskForeignServerDefinition Normalize(BlueTuskForeignServerDefinition definition) =>
        definition with
        {
            Type = NormalizeNullable(definition.Type),
            Version = NormalizeNullable(definition.Version),
            Options = NormalizeOptions(definition.Options),
        };

    public static BlueTuskUserMappingDefinition Normalize(BlueTuskUserMappingDefinition definition) =>
        definition with { Options = NormalizeOptions(definition.Options) };

    public static BlueTuskForeignTableDefinition Normalize(BlueTuskForeignTableDefinition definition) =>
        definition with
        {
            Options = NormalizeOptions(definition.Options),
            Columns = definition.Columns
                .Select(column => column with { Options = NormalizeOptions(column.Options) })
                .OrderBy(column => column.Name, StringComparer.Ordinal)
                .ToArray(),
        };

    public static void ValidateForModel(BlueTuskForeignDataDefinitionSet definitions)
    {
        Validate(definitions);
        foreach (var mapping in definitions.UserMappings)
        {
            ValidateForModel(mapping);
        }
    }

    public static void Validate(BlueTuskForeignDataDefinitionSet definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(definitions.Wrappers);
        ArgumentNullException.ThrowIfNull(definitions.Servers);
        ArgumentNullException.ThrowIfNull(definitions.UserMappings);
        ValidateUnique(definitions.Wrappers, item => item.Name, "foreign-data wrapper");
        ValidateUnique(definitions.Servers, item => item.Name, "foreign server");
        ValidateUnique(definitions.UserMappings, item => $"{item.ServerName}\0{item.UserName}", "user mapping");
        foreach (var definition in definitions.Wrappers)
        {
            Validate(definition);
        }

        foreach (var definition in definitions.Servers)
        {
            Validate(definition);
        }

        foreach (var definition in definitions.UserMappings)
        {
            Validate(definition);
        }
    }

    public static void Validate(BlueTuskForeignDataWrapperDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ValidateOptional(definition.HandlerFunction, nameof(definition.HandlerFunction));
        ValidateOptional(definition.ValidatorFunction, nameof(definition.ValidatorFunction));
        ValidateOptional(definition.ConnectionFunction, nameof(definition.ConnectionFunction));
        ValidateOptions(definition.Options);
    }

    public static void Validate(BlueTuskForeignServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ForeignDataWrapper);
        ValidateOptional(definition.Type, nameof(definition.Type));
        ValidateOptional(definition.Version, nameof(definition.Version));
        ValidateOptions(definition.Options);
    }

    public static void Validate(BlueTuskUserMappingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ServerName);
        ValidateOptional(definition.UserName, nameof(definition.UserName));
        ValidateOptions(definition.Options);
        if (definition.OptionsRedacted && definition.Options.Count > 0)
        {
            throw new ArgumentException("A redacted user mapping cannot contain option values.", nameof(definition));
        }
    }

    public static void ValidateForModel(BlueTuskUserMappingDefinition definition)
    {
        Validate(definition);
        var sensitive = definition.Options.FirstOrDefault(option => SensitiveOptionRegex().IsMatch(option.Name));
        if (sensitive is not null)
        {
            throw new ArgumentException(
                $"User-mapping option '{sensitive.Name}' can contain credentials and cannot be stored in EF model " +
                "metadata or generated C#. Supply it only from a secret source in a manually authored migration.",
                nameof(definition));
        }
    }

    public static void Validate(BlueTuskForeignTableDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ServerName);
        ValidateOptions(definition.Options);
        ArgumentNullException.ThrowIfNull(definition.Columns);
        ValidateUnique(definition.Columns, column => column.Name, "foreign-table column");
        foreach (var column in definition.Columns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(column.Name);
            ValidateOptions(column.Options);
        }
    }

    private static BlueTuskForeignOptionDefinition[] NormalizeOptions(
        IReadOnlyList<BlueTuskForeignOptionDefinition> options) =>
        options.OrderBy(option => option.Name, StringComparer.Ordinal).ToArray();

    private static void ValidateOptions(IReadOnlyList<BlueTuskForeignOptionDefinition> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateUnique(options, option => option.Name, "foreign option");
        foreach (var option in options)
        {
            ArgumentNullException.ThrowIfNull(option);
            ArgumentException.ThrowIfNullOrWhiteSpace(option.Name);
            ArgumentNullException.ThrowIfNull(option.Value);
        }
    }

    private static void ValidateUnique<T>(
        IReadOnlyList<T> items,
        Func<T, string> key,
        string objectType)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!names.Add(key(item)))
            {
                throw new ArgumentException($"A {objectType} is configured more than once.");
            }
        }
    }

    private static void ValidateOptional(string? value, string parameterName)
    {
        if (value is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        }
    }

    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("(?:password|passwd|pwd|secret|token|credential|api[_-]?key)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveOptionRegex();
}

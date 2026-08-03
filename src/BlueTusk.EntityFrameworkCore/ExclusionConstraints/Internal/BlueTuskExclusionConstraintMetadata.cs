using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.ExclusionConstraints.Internal;

internal static partial class BlueTuskExclusionConstraintMetadata
{
    public const string AnnotationName = "BlueTusk:ExclusionConstraints";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static IReadOnlyList<BlueTuskExclusionConstraintDefinition> Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? [] : Deserialize(json);
    }

    public static string Serialize(IEnumerable<BlueTuskExclusionConstraintDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var normalized = Normalize(definitions);
        Validate(normalized);
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static string Serialize(BlueTuskExclusionConstraintDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = Normalize(definition);
        Validate(normalized);
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static string Serialize(IEnumerable<BlueTuskExclusionConstraintTableDefinition> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return JsonSerializer.Serialize(
            tables.OrderBy(table => table.Schema, StringComparer.Ordinal)
                .ThenBy(table => table.Name, StringComparer.Ordinal)
                .Select(table => table with { Constraints = Normalize(table.Constraints) }),
            SerializerOptions);
    }

    public static IReadOnlyList<BlueTuskExclusionConstraintDefinition> Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskExclusionConstraintDefinition[]>(json, SerializerOptions)
            ?? throw new ArgumentException("The exclusion-constraint definition set is empty.", nameof(json));
        var normalized = Normalize(definitions);
        Validate(normalized);
        return normalized;
    }

    public static BlueTuskExclusionConstraintDefinition DeserializeDefinition(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskExclusionConstraintDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The exclusion-constraint definition is empty.", nameof(json));
        var normalized = Normalize(definition);
        Validate(normalized);
        return normalized;
    }

    public static IReadOnlyList<BlueTuskExclusionConstraintTableDefinition> GetTables(
        IRelationalModel? relationalModel)
    {
        if (relationalModel is null)
        {
            return [];
        }

        var tables = new Dictionary<
            (string? Schema, string Name),
            BlueTuskExclusionConstraintTableDefinition>();
        foreach (var entityType in relationalModel.Model.GetEntityTypes())
        {
            var definitions = Get(entityType);
            var tableName = entityType.GetTableName();
            if (definitions.Count == 0 || tableName is null)
            {
                continue;
            }

            if (BlueTuskPartitionMetadata.Get(entityType) is not null)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL does not support exclusion constraints on partitioned table " +
                    $"'{entityType.GetSchema()}.{tableName}'. Configure constraints on concrete leaf partitions explicitly.");
            }

            var schema = entityType.GetSchema();
            var normalizedDefinitions = Normalize(entityType, tableName, schema, definitions);
            Validate(normalizedDefinitions);
            var table = new BlueTuskExclusionConstraintTableDefinition(
                tableName,
                schema,
                normalizedDefinitions);
            var key = (schema, tableName);
            if (tables.TryGetValue(key, out var existing) &&
                !string.Equals(Serialize(existing.Constraints), Serialize(table.Constraints), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Entity types sharing table '{schema}.{tableName}' must use identical exclusion-constraint metadata.");
            }

            tables[key] = table;
        }

        return tables.Values.OrderBy(table => table.Schema, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static void Validate(IReadOnlyList<BlueTuskExclusionConstraintDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            Validate(definition);
            if (!names.Add(definition.Name))
            {
                throw new ArgumentException(
                    $"Exclusion constraint '{definition.Name}' is configured more than once.",
                    nameof(definitions));
            }
        }
    }

    public static void Validate(BlueTuskExclusionConstraintDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.IndexMethod);
        ArgumentNullException.ThrowIfNull(definition.Elements);
        ArgumentNullException.ThrowIfNull(definition.IncludedColumns);
        ArgumentNullException.ThrowIfNull(definition.StorageParameters);
        if (definition.Elements.Count == 0)
        {
            throw new ArgumentException("An exclusion constraint requires at least one element.", nameof(definition));
        }

        if (definition.IsInitiallyDeferred && !definition.IsDeferrable)
        {
            throw new ArgumentException(
                "An initially deferred exclusion constraint must be deferrable.",
                nameof(definition));
        }

        ValidateOptionalIdentifier(definition.Tablespace, nameof(definition.Tablespace));
        ValidateOptionalSql(definition.PredicateSql, nameof(definition.PredicateSql));
        foreach (var element in definition.Elements)
        {
            Validate(element);
        }

        ValidateUniqueIdentifiers(definition.IncludedColumns, "Included columns", nameof(definition));
        ValidateParameters(definition.StorageParameters, nameof(definition.StorageParameters));
    }

    private static void Validate(BlueTuskExclusionElementDefinition element)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrWhiteSpace(element.Expression);
        ArgumentException.ThrowIfNullOrWhiteSpace(element.Operator);
        if (!OperatorPattern().IsMatch(element.Operator) ||
            element.Operator.Contains("--", StringComparison.Ordinal) ||
            element.Operator.Contains("/*", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{element.Operator}' is not a safe PostgreSQL operator token.",
                nameof(element));
        }

        ValidateOptionalIdentifier(element.OperatorSchema, nameof(element.OperatorSchema));
        ValidateIdentifierPair(element.Collation, element.CollationSchema, nameof(element.Collation));
        ValidateIdentifierPair(element.OperatorClass, element.OperatorClassSchema, nameof(element.OperatorClass));
        ArgumentNullException.ThrowIfNull(element.OperatorClassParameters);
        ValidateParameters(element.OperatorClassParameters, nameof(element.OperatorClassParameters));
        if (!Enum.IsDefined(element.NullSortOrder))
        {
            throw new ArgumentOutOfRangeException(
                nameof(element),
                element.NullSortOrder,
                "Unknown exclusion-constraint null sort order.");
        }

        if (element.IsPreformatted &&
            (element.IsColumn ||
             element.Collation is not null ||
             element.OperatorClass is not null ||
             element.OperatorClassParameters.Count > 0 ||
             element.Descending ||
             element.NullSortOrder != BlueTuskExclusionNullSortOrder.Default))
        {
            throw new ArgumentException(
                "A preformatted exclusion element cannot also configure structured index options.",
                nameof(element));
        }
    }

    private static BlueTuskExclusionConstraintDefinition[] Normalize(
        IEnumerable<BlueTuskExclusionConstraintDefinition> definitions) =>
        definitions.Select(Normalize)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();

    private static BlueTuskExclusionConstraintDefinition Normalize(
        BlueTuskExclusionConstraintDefinition definition) =>
        definition with
        {
            Elements = definition.Elements.Select(element => element with
            {
                OperatorClassParameters = element.OperatorClassParameters
                        .OrderBy(parameter => parameter.Name, StringComparer.Ordinal)
                        .ToArray(),
            })
                .ToArray(),
            IncludedColumns = definition.IncludedColumns.ToArray(),
            StorageParameters = definition.StorageParameters
                .OrderBy(parameter => parameter.Name, StringComparer.Ordinal)
                .ToArray(),
        };

    private static BlueTuskExclusionConstraintDefinition[] Normalize(
        IReadOnlyEntityType entityType,
        string tableName,
        string? schema,
        IReadOnlyList<BlueTuskExclusionConstraintDefinition> definitions)
    {
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);
        return Normalize(definitions.Select(definition => definition with
        {
            Elements = definition.Elements.Select(element =>
                    element.IsColumn && !element.IsPreformatted
                        ? element with
                        {
                            Expression = entityType.FindProperty(element.Expression)?.GetColumnName(storeObject)
                                ?? element.Expression,
                        }
                        : element)
                .ToArray(),
            IncludedColumns = definition.IncludedColumns.Select(column =>
                    entityType.FindProperty(column)?.GetColumnName(storeObject) ?? column)
                .ToArray(),
        }));
    }

    private static void ValidateParameters(
        IReadOnlyList<BlueTuskExclusionParameterDefinition> parameters,
        string parameterName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            if (!ParameterNamePattern().IsMatch(parameter.Name))
            {
                throw new ArgumentException(
                    "Parameter names must be unquoted PostgreSQL identifiers.",
                    parameterName);
            }

            if (!ParameterValuePattern().IsMatch(parameter.Value))
            {
                throw new ArgumentException(
                    "Parameter values may contain only letters, digits, '.', '_', '+', or '-'.",
                    parameterName);
            }

            if (!names.Add(parameter.Name))
            {
                throw new ArgumentException($"Parameter '{parameter.Name}' is configured more than once.", parameterName);
            }
        }
    }

    private static void ValidateUniqueIdentifiers(
        IReadOnlyList<string> values,
        string description,
        string parameterName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (!names.Add(value))
            {
                throw new ArgumentException($"{description} must be unique.", parameterName);
            }
        }
    }

    private static void ValidateIdentifierPair(string? name, string? schema, string parameterName)
    {
        if (name is null && schema is not null)
        {
            throw new ArgumentException("A schema cannot be specified without an object name.", parameterName);
        }

        ValidateOptionalIdentifier(name, parameterName);
        ValidateOptionalIdentifier(schema, parameterName);
    }

    private static void ValidateOptionalIdentifier(string? value, string parameterName)
    {
        if (value is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        }
    }

    private static void ValidateOptionalSql(string? value, string parameterName)
    {
        if (value is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    [GeneratedRegex("^[+\\-*/<>=~!@#%^&|`?]+$", RegexOptions.CultureInvariant)]
    private static partial Regex OperatorPattern();

    [GeneratedRegex("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_.+\\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterValuePattern();
}

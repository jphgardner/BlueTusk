using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.ExpressionIndexes.Internal;

internal static partial class BlueTuskExpressionIndexMetadata
{
    public const string AnnotationName = "BlueTusk:ExpressionIndexes";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<BlueTuskExpressionIndexDefinition> Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? [] : Deserialize(json);
    }

    public static string Serialize(IEnumerable<BlueTuskExpressionIndexDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var normalized = Normalize(definitions);
        Validate(normalized);
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static string Serialize(BlueTuskExpressionIndexDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = Normalize(definition);
        Validate(normalized);
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static string Serialize(IEnumerable<BlueTuskExpressionIndexTableDefinition> tables) =>
        JsonSerializer.Serialize(
            tables.OrderBy(table => table.Schema, StringComparer.Ordinal)
                .ThenBy(table => table.Name, StringComparer.Ordinal)
                .Select(table => table with { Indexes = Normalize(table.Indexes) }),
            SerializerOptions);

    public static IReadOnlyList<BlueTuskExpressionIndexDefinition> Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskExpressionIndexDefinition[]>(json, SerializerOptions)
            ?? throw new ArgumentException("The expression-index definition set is empty.", nameof(json));
        var normalized = Normalize(definitions);
        Validate(normalized);
        return normalized;
    }

    public static BlueTuskExpressionIndexDefinition DeserializeDefinition(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskExpressionIndexDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The expression-index definition is empty.", nameof(json));
        var normalized = Normalize(definition);
        Validate(normalized);
        return normalized;
    }

    public static IReadOnlyList<BlueTuskExpressionIndexTableDefinition> GetTables(IRelationalModel? model)
    {
        if (model is null)
        {
            return [];
        }

        var tables = new Dictionary<(string? Schema, string Name), BlueTuskExpressionIndexTableDefinition>();
        foreach (var entityType in model.Model.GetEntityTypes())
        {
            var definitions = Get(entityType);
            var tableName = entityType.GetTableName();
            if (definitions.Count == 0 || tableName is null)
            {
                continue;
            }

            var table = new BlueTuskExpressionIndexTableDefinition(
                tableName,
                entityType.GetSchema(),
                definitions);
            var relationalTable = model.FindTable(table.Name, table.Schema);
            foreach (var definition in definitions)
            {
                if (relationalTable?.Indexes.Any(index =>
                        string.Equals(index.Name, definition.Name, StringComparison.Ordinal)) == true)
                {
                    throw new InvalidOperationException(
                        $"Table '{table.Schema}.{table.Name}' configures both a mapped and provider-owned index " +
                        $"named '{definition.Name}'. Index database names must be unique per schema.");
                }

                foreach (var includedColumn in definition.IncludedColumns)
                {
                    if (relationalTable?.Columns.Any(column =>
                            string.Equals(column.Name, includedColumn, StringComparison.Ordinal)) != true)
                    {
                        throw new InvalidOperationException(
                            $"Included column '{includedColumn}' is not mapped to table " +
                            $"'{table.Schema}.{table.Name}'.");
                    }
                }
            }

            var key = (table.Schema, table.Name);
            if (tables.TryGetValue(key, out var existing) &&
                !string.Equals(Serialize(existing.Indexes), Serialize(table.Indexes), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Entity types sharing table '{table.Schema}.{table.Name}' must use identical expression-index metadata.");
            }

            tables[key] = table;
        }

        return tables.Values.OrderBy(table => table.Schema, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static BlueTuskExpressionIndexDefinition Normalize(BlueTuskExpressionIndexDefinition definition) =>
        definition with
        {
            KeySql = definition.KeySql.ToArray(),
            IncludedColumns = definition.IncludedColumns.ToArray(),
            StorageParameters = definition.StorageParameters
                .OrderBy(parameter => parameter.Name, StringComparer.Ordinal)
                .ToArray(),
        };

    public static void Validate(BlueTuskExpressionIndexDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Method);
        ArgumentNullException.ThrowIfNull(definition.KeySql);
        ArgumentNullException.ThrowIfNull(definition.IncludedColumns);
        ArgumentNullException.ThrowIfNull(definition.StorageParameters);
        if (definition.KeySql.Count == 0)
        {
            throw new ArgumentException("An expression index requires at least one key.", nameof(definition));
        }

        foreach (var key in definition.KeySql)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
        }

        ValidateUniqueIdentifiers(definition.IncludedColumns, nameof(definition));
        foreach (var parameter in definition.StorageParameters)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            ValidateStorageParameter(parameter.Name, parameter.Value);
        }

        if (definition.StorageParameters.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() !=
            definition.StorageParameters.Count)
        {
            throw new ArgumentException("Index storage-parameter names must be unique.", nameof(definition));
        }

        if (definition.NullsDistinct is not null && !definition.IsUnique)
        {
            throw new ArgumentException("Null distinctness can be configured only for a unique index.", nameof(definition));
        }

        if (definition.PredicateSql is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.PredicateSql);
        }

        if (definition.Tablespace is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Tablespace);
        }
    }

    public static void ValidateStorageParameter(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!StorageParameterNamePattern().IsMatch(name))
        {
            throw new ArgumentException("Storage parameter names must be unquoted PostgreSQL identifiers.", nameof(name));
        }

        if (!StorageParameterValuePattern().IsMatch(value))
        {
            throw new ArgumentException(
                "Storage parameter values may contain only letters, digits, '.', '_', '+', or '-'.",
                nameof(value));
        }
    }

    private static BlueTuskExpressionIndexDefinition[] Normalize(
        IEnumerable<BlueTuskExpressionIndexDefinition> definitions) =>
        definitions.Select(Normalize)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();

    private static void Validate(IReadOnlyList<BlueTuskExpressionIndexDefinition> definitions)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            Validate(definition);
            if (!names.Add(definition.Name))
            {
                throw new ArgumentException(
                    $"Expression index '{definition.Name}' is configured more than once.",
                    nameof(definitions));
            }
        }
    }

    private static void ValidateUniqueIdentifiers(IEnumerable<string> values, string parameterName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (!names.Add(value))
            {
                throw new ArgumentException("Included index columns must be unique.", parameterName);
            }
        }
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageParameterNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_.+\\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageParameterValuePattern();
}

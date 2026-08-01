using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.Views.Internal;

internal static class BlueTuskViewMetadata
{
    public const string AnnotationName = "BlueTusk:Views";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static BlueTuskViewDefinitionSet Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? BlueTuskViewDefinitionSet.Empty : Deserialize(json);
    }

    public static string Serialize(BlueTuskViewDefinitionSet definitions)
    {
        Validate(definitions);
        return JsonSerializer.Serialize(Normalize(definitions), SerializerOptions);
    }

    public static string Serialize(BlueTuskViewDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static string Serialize(BlueTuskMaterializedViewDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static BlueTuskViewDefinitionSet Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskViewDefinitionSet>(json, SerializerOptions)
            ?? throw new ArgumentException("The view definition set is empty.", nameof(json));
        Validate(definitions);
        return Normalize(definitions);
    }

    public static BlueTuskViewDefinition DeserializeView(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskViewDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The view definition is empty.", nameof(json));
        Validate(definition);
        return Normalize(definition);
    }

    public static BlueTuskMaterializedViewDefinition DeserializeMaterializedView(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskMaterializedViewDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The materialized-view definition is empty.", nameof(json));
        Validate(definition);
        return Normalize(definition);
    }

    public static void Validate(BlueTuskViewDefinitionSet definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(definitions.Views);
        ArgumentNullException.ThrowIfNull(definitions.MaterializedViews);
        var names = new HashSet<ViewKey>();
        foreach (var definition in definitions.Views)
        {
            Validate(definition);
            AddName(ViewKey.Create(definition), names);
        }

        foreach (var definition in definitions.MaterializedViews)
        {
            Validate(definition);
            AddName(ViewKey.Create(definition), names);
        }
    }

    public static void Validate(BlueTuskViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateCommon(
            definition.Name,
            definition.Schema,
            definition.QuerySql,
            definition.Columns,
            definition.Dependencies);
        if (definition.IsRecursive && definition.Columns.Count == 0)
        {
            throw new ArgumentException("A recursive PostgreSQL view requires explicit output-column names.", nameof(definition));
        }

        if (definition.IsRecursive && definition.CheckOption is not null)
        {
            throw new ArgumentException("A recursive PostgreSQL view cannot use CHECK OPTION.", nameof(definition));
        }
    }

    public static void Validate(BlueTuskMaterializedViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateCommon(
            definition.Name,
            definition.Schema,
            definition.QuerySql,
            definition.Columns,
            definition.Dependencies);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.AccessMethod);
        if (definition.Tablespace is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Tablespace);
        }

        ArgumentNullException.ThrowIfNull(definition.StorageParameters);
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in definition.StorageParameters)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            ArgumentException.ThrowIfNullOrWhiteSpace(parameter.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(parameter.ValueSql);
            if (!parameterNames.Add(parameter.Name))
            {
                throw new ArgumentException(
                    $"Materialized view '{definition.Schema}.{definition.Name}' contains duplicate storage parameter '{parameter.Name}'.",
                    nameof(definition));
            }
        }
    }

    public static BlueTuskViewDefinitionSet Normalize(BlueTuskViewDefinitionSet definitions) => new(
        definitions.Views.Select(Normalize)
            .OrderBy(definition => definition.Schema, StringComparer.Ordinal)
            .ThenBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray(),
        definitions.MaterializedViews.Select(Normalize)
            .OrderBy(definition => definition.Schema, StringComparer.Ordinal)
            .ThenBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray());

    public static BlueTuskViewDefinition Normalize(BlueTuskViewDefinition definition) => definition with
    {
        QuerySql = TrimTerminator(definition.QuerySql),
        Dependencies = NormalizeDependencies(definition.Dependencies),
    };

    public static BlueTuskMaterializedViewDefinition Normalize(BlueTuskMaterializedViewDefinition definition) =>
        definition with
        {
            QuerySql = TrimTerminator(definition.QuerySql),
            Dependencies = NormalizeDependencies(definition.Dependencies),
            StorageParameters = definition.StorageParameters
                .OrderBy(parameter => parameter.Name, StringComparer.Ordinal)
                .ToArray(),
        };

    private static void ValidateCommon(
        string name,
        string? schema,
        string querySql,
        IReadOnlyList<string> columns,
        IReadOnlyList<BlueTuskViewDependencyDefinition> dependencies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (schema is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(querySql);
        ArgumentNullException.ThrowIfNull(columns);
        var columnNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(column);
            if (!columnNames.Add(column))
            {
                throw new ArgumentException($"View '{schema}.{name}' contains duplicate output column '{column}'.", nameof(columns));
            }
        }

        ArgumentNullException.ThrowIfNull(dependencies);
        var dependencyNames = new HashSet<ViewKey>();
        foreach (var dependency in dependencies)
        {
            ArgumentNullException.ThrowIfNull(dependency);
            ArgumentException.ThrowIfNullOrWhiteSpace(dependency.Name);
            if (dependency.Schema is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(dependency.Schema);
            }

            if (!dependencyNames.Add(new ViewKey(dependency.Schema, dependency.Name)))
            {
                throw new ArgumentException(
                    $"View '{schema}.{name}' contains duplicate dependency '{dependency.Schema}.{dependency.Name}'.",
                    nameof(dependencies));
            }
        }
    }

    private static BlueTuskViewDependencyDefinition[] NormalizeDependencies(
        IReadOnlyList<BlueTuskViewDependencyDefinition> dependencies) =>
        dependencies.OrderBy(dependency => dependency.Schema, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Name, StringComparer.Ordinal)
            .ToArray();

    private static string TrimTerminator(string sql)
    {
        var normalized = sql.Trim();
        return normalized.EndsWith(';') ? normalized[..^1].TrimEnd() : normalized;
    }

    private static void AddName(ViewKey key, HashSet<ViewKey> names)
    {
        if (!names.Add(key))
        {
            throw new ArgumentException(
                $"PostgreSQL relation '{key.Schema}.{key.Name}' is configured as a view more than once.");
        }
    }

    internal readonly record struct ViewKey(string? Schema, string Name)
    {
        public static ViewKey Create(BlueTuskViewDefinition definition) => new(definition.Schema, definition.Name);

        public static ViewKey Create(BlueTuskMaterializedViewDefinition definition) =>
            new(definition.Schema, definition.Name);
    }
}

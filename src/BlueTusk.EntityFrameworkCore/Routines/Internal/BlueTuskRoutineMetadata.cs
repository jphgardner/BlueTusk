using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.Routines.Internal;

internal static class BlueTuskRoutineMetadata
{
    public const string AnnotationName = "BlueTusk:Routines";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static BlueTuskRoutineDefinitionSet Get(IReadOnlyAnnotatable annotatable)
    {
        BlueTuskGuard.NotNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? BlueTuskRoutineDefinitionSet.Empty : Deserialize(json);
    }

    public static string Serialize(BlueTuskRoutineDefinitionSet definitions)
    {
        Validate(definitions);
        return JsonSerializer.Serialize(Normalize(definitions), SerializerOptions);
    }

    public static string Serialize(BlueTuskRoutineDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static BlueTuskRoutineDefinitionSet Deserialize(string json)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskRoutineDefinitionSet>(json, SerializerOptions)
            ?? throw new ArgumentException("The routine definition set is empty.", nameof(json));
        Validate(definitions);
        return Normalize(definitions);
    }

    public static BlueTuskRoutineDefinition DeserializeDefinition(string json)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskRoutineDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The routine definition is empty.", nameof(json));
        Validate(definition);
        return Normalize(definition);
    }

    public static void Validate(BlueTuskRoutineDefinitionSet definitions)
    {
        BlueTuskGuard.NotNull(definitions);
        BlueTuskGuard.NotNull(definitions.Routines);
        var keys = new HashSet<(string? Schema, string Name, string InputArgumentTypesSql)>();
        foreach (var definition in definitions.Routines)
        {
            Validate(definition);
            var normalized = Normalize(definition);
            var key = (normalized.Schema, normalized.Name, normalized.InputArgumentTypesSql);
            if (!keys.Add(key))
            {
                throw new ArgumentException(
                    $"PostgreSQL routine '{definition.Schema}.{definition.Name}({definition.InputArgumentTypesSql})' " +
                    "is configured more than once.",
                    nameof(definitions));
            }
        }
    }

    public static void Validate(BlueTuskRoutineDefinition definition)
    {
        BlueTuskGuard.NotNull(definition);
        BlueTuskGuard.NotNullOrWhiteSpace(definition.Name);
        if (definition.Schema is not null)
        {
            BlueTuskGuard.NotNullOrWhiteSpace(definition.Schema);
        }

        BlueTuskGuard.NotNull(definition.InputArgumentTypesSql);
        BlueTuskGuard.NotNull(definition.IdentityArgumentsSql);
        BlueTuskGuard.NotNull(definition.ArgumentsSql);
        if (definition.Kind == BlueTuskRoutineKind.Function)
        {
            BlueTuskGuard.NotNullOrWhiteSpace(definition.ResultSql);
        }
        else if (definition.ResultSql is not null)
        {
            throw new ArgumentException("A PostgreSQL procedure cannot declare a function result.", nameof(definition));
        }

        if (definition.Kind == BlueTuskRoutineKind.Procedure && definition.IsWindow)
        {
            throw new ArgumentException("A PostgreSQL procedure cannot be a window function.", nameof(definition));
        }

        BlueTuskGuard.NotNullOrWhiteSpace(definition.CreateOrReplaceSql);
        var expectedPrefix = definition.Kind == BlueTuskRoutineKind.Function
            ? "CREATE OR REPLACE FUNCTION "
            : "CREATE OR REPLACE PROCEDURE ";
        if (!definition.CreateOrReplaceSql.TrimStart().StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The canonical DDL for routine '{definition.Schema}.{definition.Name}' must begin with " +
                $"'{expectedPrefix.TrimEnd()}'.",
                nameof(definition));
        }
    }

    public static BlueTuskRoutineDefinitionSet Normalize(BlueTuskRoutineDefinitionSet definitions) =>
        new(definitions.Routines.Select(Normalize)
            .OrderBy(definition => definition.Schema, StringComparer.Ordinal)
            .ThenBy(definition => definition.Name, StringComparer.Ordinal)
            .ThenBy(definition => definition.InputArgumentTypesSql, StringComparer.Ordinal)
            .ThenBy(definition => definition.Kind)
            .ToArray());

    public static BlueTuskRoutineDefinition Normalize(BlueTuskRoutineDefinition definition) =>
        definition with
        {
            InputArgumentTypesSql = definition.InputArgumentTypesSql.Trim(),
            IdentityArgumentsSql = definition.IdentityArgumentsSql.Trim(),
            ArgumentsSql = definition.ArgumentsSql.Trim(),
            ResultSql = definition.ResultSql?.Trim(),
            CreateOrReplaceSql = TrimTerminator(definition.CreateOrReplaceSql),
        };

    private static string TrimTerminator(string sql)
    {
        var normalized = sql.Trim();
        return normalized.EndsWith(';') ? normalized[..^1].TrimEnd() : normalized;
    }

    internal readonly record struct RoutineKey(
        BlueTuskRoutineKind Kind,
        string? Schema,
        string Name,
        string InputArgumentTypesSql)
    {
        public static RoutineKey Create(BlueTuskRoutineDefinition definition) =>
            new(definition.Kind, definition.Schema, definition.Name, definition.InputArgumentTypesSql);
    }
}

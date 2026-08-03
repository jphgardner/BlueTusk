using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Rules.Internal;

internal static class BlueTuskRuleMetadata
{
    public const string AnnotationName = "BlueTusk:Rules";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static IReadOnlyList<BlueTuskRuleDefinition> Get(IReadOnlyAnnotatable annotatable)
    {
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? [] : Deserialize(json);
    }

    public static string Serialize(IEnumerable<BlueTuskRuleDefinition> definitions)
    {
        var normalized = Normalize(definitions);
        Validate(normalized);
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static string Serialize(BlueTuskRuleDefinition definition)
    {
        var normalized = Normalize(definition);
        Validate(normalized);
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static string Serialize(IEnumerable<BlueTuskRuleTableDefinition> tables) =>
        JsonSerializer.Serialize(
            tables.OrderBy(table => table.Schema, StringComparer.Ordinal)
                .ThenBy(table => table.Name, StringComparer.Ordinal)
                .Select(table => table with { Rules = Normalize(table.Rules) }),
            SerializerOptions);

    public static IReadOnlyList<BlueTuskRuleDefinition> Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskRuleDefinition[]>(json, SerializerOptions)
            ?? throw new ArgumentException("The rule definition set is empty.", nameof(json));
        var normalized = Normalize(definitions);
        Validate(normalized);
        return normalized;
    }

    public static BlueTuskRuleDefinition DeserializeDefinition(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskRuleDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The rule definition is empty.", nameof(json));
        var normalized = Normalize(definition);
        Validate(normalized);
        return normalized;
    }

    public static IReadOnlyList<BlueTuskRuleTableDefinition> GetTables(IRelationalModel? model)
    {
        if (model is null)
        {
            return [];
        }

        var tables = new Dictionary<(string? Schema, string Name), BlueTuskRuleTableDefinition>();
        foreach (var entityType in model.Model.GetEntityTypes())
        {
            var rules = Get(entityType);
            if (rules.Count == 0)
            {
                continue;
            }

            var tableName = entityType.GetTableName();
            var name = tableName ?? entityType.GetViewName();
            var schema = tableName is null ? entityType.GetViewSchema() : entityType.GetSchema();
            if (name is null)
            {
                continue;
            }

            var normalized = Normalize(rules);
            var definition = new BlueTuskRuleTableDefinition(name, schema, normalized);
            var key = (schema, name);
            if (tables.TryGetValue(key, out var existing) &&
                !string.Equals(Serialize(existing.Rules), Serialize(normalized), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Entity types sharing relation '{schema}.{name}' must use identical rule metadata.");
            }

            tables[key] = definition;
        }

        return tables.Values.OrderBy(table => table.Schema, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static void Validate(IReadOnlyList<BlueTuskRuleDefinition> definitions)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            Validate(definition);
            if (!names.Add(definition.Name))
            {
                throw new ArgumentException($"Rule '{definition.Name}' is configured more than once.");
            }
        }
    }

    public static void Validate(BlueTuskRuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        if (!Enum.IsDefined(definition.Event) || !Enum.IsDefined(definition.EnabledMode))
        {
            throw new ArgumentException("The rule uses an unknown enum value.", nameof(definition));
        }

        if (definition.CanonicalCreateSql is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.CanonicalCreateSql);
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ActionSql);
        if (definition.ConditionSql is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.ConditionSql);
        }

        if (definition.Event == BlueTuskRuleEvent.Select &&
            (!string.Equals(definition.Name, "_RETURN", StringComparison.Ordinal) ||
             !definition.IsInstead || definition.ConditionSql is not null))
        {
            throw new ArgumentException(
                "PostgreSQL SELECT rules must be unconditional INSTEAD rules named '_RETURN'.",
                nameof(definition));
        }
    }

    private static BlueTuskRuleDefinition[] Normalize(IEnumerable<BlueTuskRuleDefinition> definitions) =>
        definitions.Select(Normalize).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();

    private static BlueTuskRuleDefinition Normalize(BlueTuskRuleDefinition definition) =>
        definition with { CanonicalCreateSql = definition.CanonicalCreateSql?.Trim().TrimEnd(';') };

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.TableInheritance.Internal;

internal static class BlueTuskTableInheritanceMetadata
{
    public const string AnnotationName = "BlueTusk:TableInheritance";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static BlueTuskTableInheritanceDefinition? Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? null : Deserialize(json);
    }

    public static string Serialize(BlueTuskTableInheritanceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static string Serialize(IEnumerable<BlueTuskTableInheritanceTableDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return JsonSerializer.Serialize(
            definitions
                .Select(definition => definition with
                {
                    Inheritance = Normalize(definition.Inheritance, definition.Schema),
                })
                .OrderBy(definition => definition.Schema, StringComparer.Ordinal)
                .ThenBy(definition => definition.Name, StringComparer.Ordinal),
            SerializerOptions);
    }

    public static BlueTuskTableInheritanceDefinition Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskTableInheritanceDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The table-inheritance definition is empty.", nameof(json));
        definition = Normalize(definition);
        Validate(definition);
        return definition;
    }

    public static IReadOnlyList<BlueTuskTableInheritanceTableDefinition> GetTables(
        IRelationalModel? relationalModel)
    {
        if (relationalModel is null)
        {
            return Array.Empty<BlueTuskTableInheritanceTableDefinition>();
        }

        var tables = new Dictionary<
            (string? Schema, string Name),
            BlueTuskTableInheritanceTableDefinition>();
        foreach (var entityType in relationalModel.Model.GetEntityTypes())
        {
            var definition = Get(entityType);
            var tableName = entityType.GetTableName();
            if (definition is null || tableName is null)
            {
                continue;
            }

            var schema = entityType.GetSchema();
            var normalized = new BlueTuskTableInheritanceTableDefinition(
                tableName,
                schema,
                Normalize(definition, schema));
            Validate(normalized.Inheritance, tableName, schema);
            var key = (schema, tableName);
            if (tables.TryGetValue(key, out var existing) &&
                !string.Equals(
                    Serialize(existing.Inheritance),
                    Serialize(normalized.Inheritance),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Entity types sharing inherited table '{schema}.{tableName}' must use identical inheritance metadata.");
            }

            tables[key] = normalized;
        }

        return tables.Values
            .OrderBy(table => table.Schema, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static BlueTuskTableInheritanceDefinition? GetTableDefinition(ITable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (var mapping in table.EntityTypeMappings)
        {
            if (mapping.TypeBase is not IReadOnlyEntityType entityType || Get(entityType) is not { } definition)
            {
                continue;
            }

            var normalized = Normalize(definition, table.Schema);
            Validate(normalized, table.Name, table.Schema);
            return normalized;
        }

        return null;
    }

    public static void Validate(
        BlueTuskTableInheritanceDefinition definition,
        string? childName = null,
        string? childSchema = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Parents);
        if (definition.Parents.Count == 0)
        {
            throw new ArgumentException("Table inheritance requires at least one direct parent.", nameof(definition));
        }

        var parents = new HashSet<(string? Schema, string Name)>();
        foreach (var parent in definition.Parents)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentException.ThrowIfNullOrWhiteSpace(parent.Name);
            if (parent.Schema is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(parent.Schema);
            }

            var key = (parent.Schema, parent.Name);
            if (!parents.Add(key))
            {
                throw new ArgumentException(
                    $"Table inheritance contains duplicate parent '{parent.Schema}.{parent.Name}'.",
                    nameof(definition));
            }

            if (childName is not null &&
                string.Equals(parent.Name, childName, StringComparison.Ordinal) &&
                string.Equals(parent.Schema, childSchema, StringComparison.Ordinal))
            {
                throw new ArgumentException("A PostgreSQL table cannot inherit from itself.", nameof(definition));
            }
        }
    }

    private static BlueTuskTableInheritanceDefinition Normalize(
        BlueTuskTableInheritanceDefinition definition,
        string? childSchema = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Parents);
        return definition with
        {
            Parents = definition.Parents
                .Select(parent => parent with { Schema = parent.Schema ?? childSchema })
                .ToArray(),
        };
    }
}

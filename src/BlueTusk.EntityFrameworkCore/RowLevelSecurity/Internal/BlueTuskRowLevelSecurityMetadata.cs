using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;

internal static class BlueTuskRowLevelSecurityMetadata
{
    public const string AnnotationName = "BlueTusk:RowLevelSecurity";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static BlueTuskRowLevelSecurityDefinition? Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? null : Deserialize(json);
    }

    public static string Serialize(BlueTuskRowLevelSecurityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static string Serialize(BlueTuskRowSecurityPolicyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static string Serialize(IEnumerable<BlueTuskRowLevelSecurityTableDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return JsonSerializer.Serialize(
            definitions.OrderBy(definition => definition.Schema, StringComparer.Ordinal)
                .ThenBy(definition => definition.Name, StringComparer.Ordinal)
                .Select(definition => definition with
                {
                    RowLevelSecurity = Normalize(definition.RowLevelSecurity),
                }),
            SerializerOptions);
    }

    public static BlueTuskRowLevelSecurityDefinition Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return Normalize(
            JsonSerializer.Deserialize<BlueTuskRowLevelSecurityDefinition>(json, SerializerOptions)
                ?? throw new ArgumentException("The row-level security definition is empty.", nameof(json)));
    }

    public static BlueTuskRowSecurityPolicyDefinition DeserializePolicy(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return Normalize(
            JsonSerializer.Deserialize<BlueTuskRowSecurityPolicyDefinition>(json, SerializerOptions)
                ?? throw new ArgumentException("The row-security policy definition is empty.", nameof(json)));
    }

    public static IReadOnlyList<BlueTuskRowLevelSecurityTableDefinition> GetTables(
        IRelationalModel? relationalModel)
    {
        if (relationalModel is null)
        {
            return Array.Empty<BlueTuskRowLevelSecurityTableDefinition>();
        }

        var tables = new Dictionary<(string? Schema, string Name), BlueTuskRowLevelSecurityTableDefinition>();
        foreach (var entityType in relationalModel.Model.GetEntityTypes())
        {
            var definition = Get(entityType);
            var tableName = entityType.GetTableName();
            if (definition is null || tableName is null)
            {
                continue;
            }

            BlueTuskRowLevelSecurityBuilder.ValidateDefinition(definition);
            var normalized = Normalize(definition);
            var table = new BlueTuskRowLevelSecurityTableDefinition(
                tableName,
                entityType.GetSchema(),
                normalized);
            var key = (table.Schema, table.Name);
            if (tables.TryGetValue(key, out var existing) &&
                !string.Equals(
                    Serialize(existing.RowLevelSecurity),
                    Serialize(table.RowLevelSecurity),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Entity types sharing row-secured table '{table.Schema}.{table.Name}' must use identical metadata.");
            }

            tables[key] = table;
        }

        return tables.Values
            .OrderBy(table => table.Schema, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static BlueTuskRowLevelSecurityDefinition? GetTableDefinition(ITable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (var mapping in table.EntityTypeMappings)
        {
            if (mapping.TypeBase is IReadOnlyEntityType entityType && Get(entityType) is { } definition)
            {
                return Normalize(definition) with
                {
                    Policies = Array.Empty<BlueTuskRowSecurityPolicyDefinition>(),
                };
            }
        }

        return null;
    }

    private static BlueTuskRowLevelSecurityDefinition Normalize(
        BlueTuskRowLevelSecurityDefinition definition) =>
        definition with
        {
            Policies = definition.Policies
                .Select(Normalize)
                .OrderBy(policy => policy.Name, StringComparer.Ordinal)
                .ToArray(),
        };

    private static BlueTuskRowSecurityPolicyDefinition Normalize(
        BlueTuskRowSecurityPolicyDefinition definition) =>
        definition with
        {
            Roles = definition.Roles
                .OrderBy(role => role.Kind)
                .ThenBy(role => role.Name, StringComparer.Ordinal)
                .ToArray(),
        };

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

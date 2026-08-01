using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Partitioning.Internal;

internal static class BlueTuskPartitionMetadata
{
    public const string AnnotationName = "BlueTusk:Partitioning";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static BlueTuskPartitioningDefinition? Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? null : Deserialize(json);
    }

    public static string Serialize(BlueTuskPartitioningDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, SerializerOptions);
    }

    public static string Serialize(BlueTuskPartitionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, SerializerOptions);
    }

    public static string Serialize(BlueTuskPartitionBound bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        return JsonSerializer.Serialize(bound, SerializerOptions);
    }

    public static string Serialize(BlueTuskPartitionedTableDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, SerializerOptions);
    }

    public static string Serialize(IEnumerable<BlueTuskPartitionedTableDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return JsonSerializer.Serialize(
            definitions.OrderBy(definition => definition.Schema, StringComparer.Ordinal)
                .ThenBy(definition => definition.Name, StringComparer.Ordinal),
            SerializerOptions);
    }

    public static BlueTuskPartitioningDefinition Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<BlueTuskPartitioningDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The partitioning definition is empty.", nameof(json));
    }

    public static BlueTuskPartitionDefinition DeserializePartition(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<BlueTuskPartitionDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The partition definition is empty.", nameof(json));
    }

    public static BlueTuskPartitionBound DeserializeBound(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<BlueTuskPartitionBound>(json, SerializerOptions)
            ?? throw new ArgumentException("The partition bound is empty.", nameof(json));
    }

    public static IReadOnlyList<BlueTuskPartitionedTableDefinition> GetTables(IRelationalModel? relationalModel)
    {
        if (relationalModel is null)
        {
            return Array.Empty<BlueTuskPartitionedTableDefinition>();
        }

        var tables = new Dictionary<(string? Schema, string Name), BlueTuskPartitionedTableDefinition>();
        foreach (var entityType in relationalModel.Model.GetEntityTypes())
        {
            var definition = Get(entityType);
            var tableName = entityType.GetTableName();
            if (definition is null || tableName is null)
            {
                continue;
            }

            var schema = entityType.GetSchema();
            var normalizedDefinition = Normalize(entityType, tableName, schema, definition);
            BlueTuskPartitioningBuilder.ValidateDefinition(normalizedDefinition);
            var normalized = new BlueTuskPartitionedTableDefinition(
                tableName,
                schema,
                normalizedDefinition);
            var key = (schema, tableName);
            if (tables.TryGetValue(key, out var existing) &&
                !string.Equals(Serialize(existing.Partitioning), Serialize(normalized.Partitioning), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Entity types sharing partitioned table '{schema}.{tableName}' must use identical partitioning metadata.");
            }

            tables[key] = normalized;
        }

        return tables.Values
            .OrderBy(table => table.Schema, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static BlueTuskPartitioningDefinition? GetTableDefinition(ITable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (var mapping in table.EntityTypeMappings)
        {
            if (mapping.TypeBase is not IReadOnlyEntityType entityType || Get(entityType) is not { } definition)
            {
                continue;
            }

            return Normalize(entityType, table.Name, table.Schema, definition) with
            {
                Partitions = Array.Empty<BlueTuskPartitionDefinition>(),
            };
        }

        return null;
    }

    private static BlueTuskPartitioningDefinition Normalize(
        IReadOnlyEntityType entityType,
        string tableName,
        string? tableSchema,
        BlueTuskPartitioningDefinition definition)
    {
        var storeObject = StoreObjectIdentifier.Table(tableName, tableSchema);
        return Normalize(entityType, storeObject, tableSchema, definition);
    }

    private static BlueTuskPartitioningDefinition Normalize(
        IReadOnlyEntityType entityType,
        StoreObjectIdentifier storeObject,
        string? parentSchema,
        BlueTuskPartitioningDefinition definition)
    {
        var keys = definition.Keys.Select(
                key => key.IsColumn
                    ? key with
                    {
                        Expression = entityType.FindProperty(key.Expression)?.GetColumnName(storeObject)
                            ?? key.Expression,
                    }
                    : key)
            .ToArray();
        var partitions = definition.Partitions.Select(
                partition =>
                {
                    var schema = partition.Schema ?? parentSchema;
                    return partition with
                    {
                        Schema = schema,
                        Partitioning = partition.Partitioning is null
                            ? null
                            : Normalize(entityType, storeObject, schema, partition.Partitioning),
                    };
                })
            .ToArray();
        return definition with { Keys = keys, Partitions = partitions };
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

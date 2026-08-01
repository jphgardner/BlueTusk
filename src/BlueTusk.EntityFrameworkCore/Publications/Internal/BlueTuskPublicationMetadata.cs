using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.Publications.Internal;

internal static class BlueTuskPublicationMetadata
{
    public const string AnnotationName = "BlueTusk:Publications";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static BlueTuskPublicationDefinitionSet Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? BlueTuskPublicationDefinitionSet.Empty : Deserialize(json);
    }

    public static string Serialize(BlueTuskPublicationDefinitionSet definitions)
    {
        Validate(definitions);
        return JsonSerializer.Serialize(Normalize(definitions), SerializerOptions);
    }

    public static string Serialize(BlueTuskPublicationDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static BlueTuskPublicationDefinitionSet Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskPublicationDefinitionSet>(json, SerializerOptions)
            ?? throw new ArgumentException("The publication definition set is empty.", nameof(json));
        Validate(definitions);
        return Normalize(definitions);
    }

    public static BlueTuskPublicationDefinition DeserializeDefinition(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskPublicationDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The publication definition is empty.", nameof(json));
        Validate(definition);
        return Normalize(definition);
    }

    public static void Validate(BlueTuskPublicationDefinitionSet definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(definitions.Publications);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions.Publications)
        {
            Validate(definition);
            if (!names.Add(definition.Name))
            {
                throw new ArgumentException($"Publication '{definition.Name}' is configured more than once.");
            }
        }
    }

    public static void Validate(BlueTuskPublicationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ArgumentNullException.ThrowIfNull(definition.Tables);
        ArgumentNullException.ThrowIfNull(definition.Schemas);
        if ((definition.Operations & ~BlueTuskPublicationOperations.All) != 0 ||
            !Enum.IsDefined(definition.GeneratedColumns))
        {
            throw new ArgumentException("The publication uses an unknown enum value.", nameof(definition));
        }

        var included = definition.Tables.Where(table => !table.IsExcluded).ToArray();
        var excluded = definition.Tables.Where(table => table.IsExcluded).ToArray();
        if (definition.AllTables && (included.Length > 0 || definition.Schemas.Count > 0))
        {
            throw new ArgumentException("A FOR ALL TABLES publication cannot also list included tables or schemas.");
        }

        if (definition.AllSequences && (included.Length > 0 || definition.Schemas.Count > 0))
        {
            throw new ArgumentException("A FOR ALL SEQUENCES publication cannot also list tables or schemas.");
        }

        if (!definition.AllTables && excluded.Length > 0)
        {
            throw new ArgumentException("Publication exclusions require FOR ALL TABLES.");
        }

        if (!definition.AllTables && !definition.AllSequences && included.Length == 0 &&
            definition.Schemas.Count == 0 &&
            (definition.Operations != BlueTuskPublicationOperations.All ||
             definition.PublishViaPartitionRoot ||
             definition.GeneratedColumns != BlueTuskPublicationGeneratedColumns.None))
        {
            throw new ArgumentException("An empty publication cannot configure table publication options.");
        }

        if (definition.AllSequences && !definition.AllTables &&
            (definition.Operations != BlueTuskPublicationOperations.All ||
             definition.PublishViaPartitionRoot ||
             definition.GeneratedColumns != BlueTuskPublicationGeneratedColumns.None))
        {
            throw new ArgumentException("A sequence-only publication cannot configure table publication options.");
        }

        var schemas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schema in definition.Schemas)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(schema);
            if (!schemas.Add(schema))
            {
                throw new ArgumentException($"Publication schema '{schema}' is configured more than once.");
            }
        }

        var tables = new HashSet<(string? Schema, string Name)>();
        foreach (var table in definition.Tables)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(table.Name);
            if (table.Schema is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(table.Schema);
            }

            if (!tables.Add((table.Schema, table.Name)))
            {
                throw new ArgumentException($"Publication table '{table.Schema}.{table.Name}' is configured more than once.");
            }

            if (table.IsExcluded && (table.Columns is not null || table.RowFilterSql is not null))
            {
                throw new ArgumentException("Excluded publication tables cannot define columns or row filters.");
            }

            if (table.Columns is not null)
            {
                if (table.Columns.Count == 0)
                {
                    throw new ArgumentException("A publication column list cannot be empty.");
                }

                var columns = new HashSet<string>(StringComparer.Ordinal);
                foreach (var column in table.Columns)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(column);
                    if (!columns.Add(column))
                    {
                        throw new ArgumentException($"Publication column '{column}' is configured more than once.");
                    }
                }

                if (definition.Schemas.Count > 0)
                {
                    throw new ArgumentException(
                        "PostgreSQL cannot combine publication schema membership with a table column list.");
                }
            }

            if (table.RowFilterSql is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(table.RowFilterSql);
            }
        }
    }

    public static BlueTuskPublicationDefinitionSet Normalize(BlueTuskPublicationDefinitionSet definitions) =>
        new(definitions.Publications.Select(Normalize)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray());

    public static BlueTuskPublicationDefinition Normalize(BlueTuskPublicationDefinition definition) =>
        definition with
        {
            Tables = definition.Tables.Select(table => table with
            {
                Columns = table.Columns?.Order(StringComparer.Ordinal).ToArray(),
                RowFilterSql = table.RowFilterSql?.Trim(),
            })
                .OrderBy(table => table.IsExcluded)
                .ThenBy(table => table.Schema, StringComparer.Ordinal)
                .ThenBy(table => table.Name, StringComparer.Ordinal)
                .ToArray(),
            Schemas = definition.Schemas.Order(StringComparer.Ordinal).ToArray(),
        };

    public static int MinimumServerVersion(BlueTuskPublicationDefinition definition)
    {
        if (definition.AllSequences || definition.Tables.Any(table => table.IsExcluded))
        {
            return 190000;
        }

        return definition.GeneratedColumns == BlueTuskPublicationGeneratedColumns.Stored ? 180000 : 150000;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

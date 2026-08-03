using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Triggers.Internal;

internal static class BlueTuskTriggerMetadata
{
    public const string AnnotationName = "BlueTusk:Triggers";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static IReadOnlyList<BlueTuskTriggerDefinition> Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? [] : Deserialize(json);
    }

    public static string Serialize(IEnumerable<BlueTuskTriggerDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var normalized = Normalize(definitions);
        Validate(normalized);
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static string Serialize(BlueTuskTriggerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = Normalize(definition);
        Validate(normalized);
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static string Serialize(IEnumerable<BlueTuskTriggerTableDefinition> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return JsonSerializer.Serialize(
            tables.OrderBy(table => table.Schema, StringComparer.Ordinal)
                .ThenBy(table => table.Name, StringComparer.Ordinal)
                .Select(table => table with { Triggers = Normalize(table.Triggers) }),
            SerializerOptions);
    }

    public static IReadOnlyList<BlueTuskTriggerDefinition> Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskTriggerDefinition[]>(json, SerializerOptions)
            ?? throw new ArgumentException("The trigger definition set is empty.", nameof(json));
        var normalized = Normalize(definitions);
        Validate(normalized);
        return normalized;
    }

    public static BlueTuskTriggerDefinition DeserializeDefinition(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskTriggerDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The trigger definition is empty.", nameof(json));
        var normalized = Normalize(definition);
        Validate(normalized);
        return normalized;
    }

    public static IReadOnlyList<BlueTuskTriggerTableDefinition> GetTables(IRelationalModel? relationalModel)
    {
        if (relationalModel is null)
        {
            return [];
        }

        var tables = new Dictionary<(string? Schema, string Name), BlueTuskTriggerTableDefinition>();
        foreach (var entityType in relationalModel.Model.GetEntityTypes())
        {
            var definitions = Get(entityType);
            if (definitions.Count == 0)
            {
                continue;
            }

            var name = entityType.GetTableName() ?? entityType.GetViewName();
            var schema = entityType.GetTableName() is null
                ? entityType.GetViewSchema()
                : entityType.GetSchema();
            if (name is null)
            {
                continue;
            }

            var normalized = Normalize(entityType, name, schema, definitions);
            Validate(normalized);
            var table = new BlueTuskTriggerTableDefinition(name, schema, normalized);
            var key = (schema, name);
            if (tables.TryGetValue(key, out var existing) &&
                !string.Equals(Serialize(existing.Triggers), Serialize(table.Triggers), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Entity types sharing relation '{schema}.{name}' must use identical trigger metadata.");
            }

            tables[key] = table;
        }

        return tables.Values.OrderBy(table => table.Schema, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static void Validate(IReadOnlyList<BlueTuskTriggerDefinition> definitions)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            Validate(definition);
            if (!names.Add(definition.Name))
            {
                throw new ArgumentException($"Trigger '{definition.Name}' is configured more than once.");
            }
        }
    }

    public static void Validate(BlueTuskTriggerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ArgumentNullException.ThrowIfNull(definition.Events);
        ArgumentNullException.ThrowIfNull(definition.Arguments);
        ValidateOptionalIdentifier(definition.ExtensionDependency, nameof(definition.ExtensionDependency));
        if (!Enum.IsDefined(definition.Timing) ||
            !Enum.IsDefined(definition.Orientation) ||
            !Enum.IsDefined(definition.EnabledMode))
        {
            throw new ArgumentException("The trigger uses an unknown enum value.", nameof(definition));
        }

        if (definition.CanonicalCreateSql is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.CanonicalCreateSql);
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(definition.FunctionName);
        ValidateIdentifierPair(definition.FunctionName, definition.FunctionSchema, nameof(definition.FunctionName));
        ValidateIdentifierPair(definition.ReferencedTable, definition.ReferencedTableSchema, nameof(definition.ReferencedTable));
        ValidateOptionalIdentifier(definition.OldTransitionTable, nameof(definition.OldTransitionTable));
        ValidateOptionalIdentifier(definition.NewTransitionTable, nameof(definition.NewTransitionTable));
        ValidateOptionalSql(definition.WhenSql, nameof(definition.WhenSql));
        if (definition.Events.Count == 0)
        {
            throw new ArgumentException("A trigger requires at least one event.", nameof(definition));
        }

        var eventKinds = new HashSet<BlueTuskTriggerEventKind>();
        foreach (var triggerEvent in definition.Events)
        {
            ArgumentNullException.ThrowIfNull(triggerEvent);
            if (!Enum.IsDefined(triggerEvent.Kind) || !eventKinds.Add(triggerEvent.Kind))
            {
                throw new ArgumentException("Trigger event kinds must be valid and unique.", nameof(definition));
            }

            ArgumentNullException.ThrowIfNull(triggerEvent.UpdateColumns);
            if (triggerEvent.Kind != BlueTuskTriggerEventKind.Update && triggerEvent.UpdateColumns.Count > 0)
            {
                throw new ArgumentException("Only UPDATE trigger events can select columns.", nameof(definition));
            }

            ValidateUniqueIdentifiers(triggerEvent.UpdateColumns, nameof(definition));
        }

        if (definition.Events.Any(item => item.Kind == BlueTuskTriggerEventKind.Truncate) &&
            definition.Orientation != BlueTuskTriggerOrientation.Statement)
        {
            throw new ArgumentException("TRUNCATE triggers must be statement-level.", nameof(definition));
        }

        if (definition.Timing == BlueTuskTriggerTiming.InsteadOf &&
            (definition.Orientation != BlueTuskTriggerOrientation.Row ||
             definition.WhenSql is not null ||
             definition.IsConstraint ||
             definition.Events.Any(item => item.Kind == BlueTuskTriggerEventKind.Truncate)))
        {
            throw new ArgumentException(
                "INSTEAD OF triggers must be non-constraint row triggers without WHEN or TRUNCATE.",
                nameof(definition));
        }

        if (definition.IsConstraint &&
            (definition.Timing != BlueTuskTriggerTiming.After ||
             definition.Orientation != BlueTuskTriggerOrientation.Row ||
             definition.OldTransitionTable is not null ||
             definition.NewTransitionTable is not null))
        {
            throw new ArgumentException(
                "Constraint triggers must be AFTER ROW triggers without transition tables.",
                nameof(definition));
        }

        if (!definition.IsConstraint &&
            (definition.ReferencedTable is not null || definition.IsDeferrable || definition.IsInitiallyDeferred))
        {
            throw new ArgumentException(
                "Referenced-table and deferrability options require a constraint trigger.",
                nameof(definition));
        }

        if (definition.IsInitiallyDeferred && !definition.IsDeferrable)
        {
            throw new ArgumentException("An initially deferred trigger must be deferrable.", nameof(definition));
        }

        ValidateTransitionTables(definition, eventKinds);
        foreach (var argument in definition.Arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
        }
    }

    private static void ValidateTransitionTables(
        BlueTuskTriggerDefinition definition,
        HashSet<BlueTuskTriggerEventKind> eventKinds)
    {
        if (definition.OldTransitionTable is null && definition.NewTransitionTable is null)
        {
            return;
        }

        if (definition.Timing != BlueTuskTriggerTiming.After || definition.IsConstraint || definition.Events.Count != 1)
        {
            throw new ArgumentException(
                "Transition tables require one non-constraint AFTER event.",
                nameof(definition));
        }

        if (definition.Events[0].UpdateColumns.Count > 0)
        {
            throw new ArgumentException("Transition tables cannot be combined with UPDATE OF columns.", nameof(definition));
        }

        if (definition.OldTransitionTable is not null &&
            !eventKinds.Contains(BlueTuskTriggerEventKind.Update) &&
            !eventKinds.Contains(BlueTuskTriggerEventKind.Delete))
        {
            throw new ArgumentException("OLD TABLE requires an UPDATE or DELETE event.", nameof(definition));
        }

        if (definition.NewTransitionTable is not null &&
            !eventKinds.Contains(BlueTuskTriggerEventKind.Update) &&
            !eventKinds.Contains(BlueTuskTriggerEventKind.Insert))
        {
            throw new ArgumentException("NEW TABLE requires an UPDATE or INSERT event.", nameof(definition));
        }
    }

    private static BlueTuskTriggerDefinition[] Normalize(IEnumerable<BlueTuskTriggerDefinition> definitions) =>
        definitions.Select(Normalize)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();

    private static BlueTuskTriggerDefinition Normalize(BlueTuskTriggerDefinition definition) =>
        definition with
        {
            Events = definition.Events.Select(item => item with { UpdateColumns = item.UpdateColumns.ToArray() })
                .OrderBy(item => item.Kind)
                .ToArray(),
            Arguments = definition.Arguments.ToArray(),
            CanonicalCreateSql = definition.CanonicalCreateSql?.Trim().TrimEnd(';'),
        };

    private static BlueTuskTriggerDefinition[] Normalize(
        IReadOnlyEntityType entityType,
        string relationName,
        string? schema,
        IReadOnlyList<BlueTuskTriggerDefinition> definitions)
    {
        var storeObject = entityType.GetTableName() is null
            ? StoreObjectIdentifier.View(relationName, schema)
            : StoreObjectIdentifier.Table(relationName, schema);
        return Normalize(definitions.Select(definition => definition with
        {
            Events = definition.Events.Select(item => item with
            {
                UpdateColumns = item.UpdateColumns.Select(column =>
                        entityType.FindProperty(column)?.GetColumnName(storeObject) ?? column)
                    .ToArray(),
            }).ToArray(),
        }));
    }

    private static void ValidateUniqueIdentifiers(IReadOnlyList<string> values, string parameterName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (!names.Add(value))
            {
                throw new ArgumentException("Trigger UPDATE columns must be unique.", parameterName);
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
}

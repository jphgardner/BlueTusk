using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Routines;
using BlueTusk.EntityFrameworkCore.Routines.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskRoutineModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskRoutineMetadata.Serialize(GetDefinitions(source)),
            BlueTuskRoutineMetadata.Serialize(GetDefinitions(target)),
            StringComparison.Ordinal);

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        IReadOnlyList<MigrationOperation> baseOperations,
        ICollection<MigrationOperation> beforeRelational,
        ICollection<MigrationOperation> afterRelational)
    {
        var source = GetDefinitions(sourceModel).Routines.ToDictionary(BlueTuskRoutineMetadata.RoutineKey.Create);
        var target = GetDefinitions(targetModel).Routines.ToDictionary(BlueTuskRoutineMetadata.RoutineKey.Create);
        RejectKindChanges(source.Values, target.Values);
        var creates = new List<BlueTuskRoutineDefinition>();
        var drops = new List<BlueTuskRoutineDefinition>();
        foreach (var (key, oldDefinition) in source)
        {
            if (!target.TryGetValue(key, out var definition))
            {
                drops.Add(oldDefinition);
                continue;
            }

            if (DefinitionEquals(oldDefinition, definition))
            {
                continue;
            }

            BlueTuskRoutineAlterationPlanner.ValidateReplacement(oldDefinition, definition);
            AddByPhase(
                new ReplaceBlueTuskRoutineOperation
                {
                    OldDefinition = oldDefinition,
                    Definition = definition,
                },
                before: !definition.HasTrackedBodyDependencies,
                beforeRelational,
                afterRelational);
        }

        creates.AddRange(target
            .Where(item => !source.ContainsKey(item.Key))
            .Select(item => item.Value));
        var schemas = creates.Select(definition => definition.Schema)
            .Where(schema => schema is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(schema => !baseOperations.OfType<EnsureSchemaOperation>()
                .Any(operation => string.Equals(operation.Name, schema, StringComparison.Ordinal)));
        foreach (var schema in schemas)
        {
            beforeRelational.Add(new EnsureSchemaOperation { Name = schema });
        }

        foreach (var definition in Order(creates))
        {
            AddByPhase(
                new CreateBlueTuskRoutineOperation { Definition = definition },
                before: !definition.HasTrackedBodyDependencies,
                beforeRelational,
                afterRelational);
        }

        foreach (var definition in Order(drops).Reverse())
        {
            var operation = new DropBlueTuskRoutineOperation
            {
                Kind = definition.Kind,
                Name = definition.Name,
                Schema = definition.Schema,
                IdentityArgumentsSql = definition.IdentityArgumentsSql,
                IsDestructiveChange = true,
            };
            AddByPhase(
                operation,
                before: definition.HasTrackedBodyDependencies,
                beforeRelational,
                afterRelational);
        }
    }

    private static BlueTuskRoutineDefinitionSet GetDefinitions(IRelationalModel? model) =>
        model is null ? BlueTuskRoutineDefinitionSet.Empty : BlueTuskRoutineMetadata.Get(model.Model);

    private static bool DefinitionEquals(
        BlueTuskRoutineDefinition left,
        BlueTuskRoutineDefinition right) =>
        string.Equals(
            BlueTuskRoutineMetadata.Serialize(left),
            BlueTuskRoutineMetadata.Serialize(right),
            StringComparison.Ordinal);

    private static void RejectKindChanges(
        IEnumerable<BlueTuskRoutineDefinition> source,
        IEnumerable<BlueTuskRoutineDefinition> target)
    {
        var sourceByPhysicalKey = source.ToDictionary(PhysicalKey.Create);
        var targetByPhysicalKey = target.ToDictionary(PhysicalKey.Create);
        foreach (var key in sourceByPhysicalKey.Keys.Intersect(targetByPhysicalKey.Keys))
        {
            if (sourceByPhysicalKey[key].Kind != targetByPhysicalKey[key].Kind)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL routine '{key.Schema}.{key.Name}({key.InputArgumentTypesSql})' cannot change " +
                    "between FUNCTION and PROCEDURE in place. Use an explicit drop/recreate migration.");
            }
        }
    }

    private static IEnumerable<BlueTuskRoutineDefinition> Order(
        IEnumerable<BlueTuskRoutineDefinition> definitions) =>
        definitions.OrderBy(definition => definition.Schema, StringComparer.Ordinal)
            .ThenBy(definition => definition.Name, StringComparer.Ordinal)
            .ThenBy(definition => definition.InputArgumentTypesSql, StringComparer.Ordinal)
            .ThenBy(definition => definition.Kind);

    private static void AddByPhase(
        MigrationOperation operation,
        bool before,
        ICollection<MigrationOperation> beforeRelational,
        ICollection<MigrationOperation> afterRelational)
    {
        (before ? beforeRelational : afterRelational).Add(operation);
    }

    private readonly record struct PhysicalKey(
        string? Schema,
        string Name,
        string InputArgumentTypesSql)
    {
        public static PhysicalKey Create(BlueTuskRoutineDefinition definition) =>
            new(definition.Schema, definition.Name, definition.InputArgumentTypesSql);
    }
}

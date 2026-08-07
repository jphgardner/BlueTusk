using BlueTusk.EntityFrameworkCore.Extensions;
using BlueTusk.EntityFrameworkCore.Extensions.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskExtensionModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskExtensionMetadata.Serialize(GetDefinitions(source)),
            BlueTuskExtensionMetadata.Serialize(GetDefinitions(target)),
            StringComparison.Ordinal);

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        IReadOnlyList<MigrationOperation> baseOperations,
        ICollection<MigrationOperation> beforeObjects,
        ICollection<MigrationOperation> afterObjects)
    {
        var source = GetDefinitions(sourceModel).Extensions.ToDictionary(
            definition => definition.Name,
            StringComparer.Ordinal);
        var target = GetDefinitions(targetModel).Extensions.ToDictionary(
            definition => definition.Name,
            StringComparer.Ordinal);
        var creates = target.Values.Where(definition => !source.ContainsKey(definition.Name))
            .ToArray();
        var alterations = target.Values
            .Where(definition => source.TryGetValue(definition.Name, out var oldDefinition) &&
                                 !StateEquals(oldDefinition, definition))
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();
        var schemas = creates.Select(definition => definition.Schema)
            .Concat(alterations.Where(definition =>
                    !string.Equals(source[definition.Name].Schema, definition.Schema, StringComparison.Ordinal))
                .Select(definition => definition.Schema))
            .Where(schema => schema is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(schema => !baseOperations.OfType<EnsureSchemaOperation>()
                .Any(operation => string.Equals(operation.Name, schema, StringComparison.Ordinal)));
        foreach (var schema in schemas)
        {
            beforeObjects.Add(new EnsureSchemaOperation { Name = schema });
        }

        foreach (var definition in OrderByDependencies(creates, target))
        {
            beforeObjects.Add(new CreateExtensionOperation { Definition = definition });
        }

        foreach (var definition in alterations)
        {
            var oldDefinition = source[definition.Name];
            if (oldDefinition.Schema is not null && definition.Schema is null)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL extension '{definition.Name}' cannot be moved to an unspecified schema. " +
                    "Configure the target schema explicitly or stage a manual migration.");
            }

            beforeObjects.Add(new AlterExtensionOperation
            {
                OldDefinition = oldDefinition,
                Definition = definition,
            });
        }

        var drops = source.Values.Where(definition => !target.ContainsKey(definition.Name)).ToArray();
        foreach (var definition in OrderByDependencies(drops, source).AsEnumerable().Reverse())
        {
            afterObjects.Add(new DropExtensionOperation
            {
                Name = definition.Name,
                IsDestructiveChange = true,
            });
        }
    }

    private static bool StateEquals(
        BlueTuskExtensionDefinition left,
        BlueTuskExtensionDefinition right) =>
        string.Equals(left.Schema, right.Schema, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal);

    private static BlueTuskExtensionDefinitionSet GetDefinitions(IRelationalModel? model) =>
        model is null ? BlueTuskExtensionDefinitionSet.Empty : BlueTuskExtensionMetadata.Get(model.Model);

    private static List<BlueTuskExtensionDefinition> OrderByDependencies(
        IEnumerable<BlueTuskExtensionDefinition> definitions,
        Dictionary<string, BlueTuskExtensionDefinition> allDefinitions)
    {
        var selected = definitions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<BlueTuskExtensionDefinition>();
        foreach (var definition in selected.Values.OrderBy(definition => definition.Name, StringComparer.Ordinal))
        {
            Visit(definition);
        }

        return ordered;

        void Visit(BlueTuskExtensionDefinition definition)
        {
            if (!visited.Add(definition.Name))
            {
                return;
            }

            foreach (var dependency in definition.Dependencies)
            {
                if (allDefinitions.ContainsKey(dependency) && selected.TryGetValue(dependency, out var selectedDependency))
                {
                    Visit(selectedDependency);
                }
            }

            ordered.Add(definition);
        }
    }
}

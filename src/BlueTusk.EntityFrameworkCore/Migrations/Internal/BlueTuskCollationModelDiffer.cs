using BlueTusk.EntityFrameworkCore.Collations;
using BlueTusk.EntityFrameworkCore.Collations.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskCollationModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskCollationMetadata.Serialize(GetDefinitions(source)),
            BlueTuskCollationMetadata.Serialize(GetDefinitions(target)),
            StringComparison.Ordinal);

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        IReadOnlyList<MigrationOperation> baseOperations,
        ICollection<MigrationOperation> beforeObjects,
        ICollection<MigrationOperation> afterObjects)
    {
        var source = GetDefinitions(sourceModel).Collations.ToDictionary(GetKey);
        var target = GetDefinitions(targetModel).Collations.ToDictionary(GetKey);
        var unmatchedTargets = new Dictionary<CollationKey, BlueTuskCollationDefinition>(target);
        var creates = new List<BlueTuskCollationDefinition>();
        var drops = new List<BlueTuskCollationDefinition>();
        var renames = new List<RenameCollationOperation>();
        var unmatchedSources = source.Values
            .Where(definition => !target.ContainsKey(GetKey(definition)))
            .ToArray();

        foreach (var (key, sourceDefinition) in source)
        {
            if (target.TryGetValue(key, out var targetDefinition))
            {
                unmatchedTargets.Remove(key);
                if (!BodyEquals(sourceDefinition, targetDefinition))
                {
                    throw new InvalidOperationException(
                        $"PostgreSQL collation '{key.Schema}.{key.Name}' cannot change its provider definition in place. " +
                        "Rebuild every dependent object and use an explicit drop/create migration.");
                }

                continue;
            }

            var candidates = unmatchedTargets.Values
                .Where(candidate => BodyEquals(sourceDefinition, candidate))
                .ToArray();
            if (candidates.Length == 1 &&
                unmatchedSources.Count(candidate => BodyEquals(candidate, candidates[0])) == 1)
            {
                var renamed = candidates[0];
                if (sourceDefinition.Schema is not null && renamed.Schema is null)
                {
                    throw new InvalidOperationException(
                        $"PostgreSQL collation '{sourceDefinition.Schema}.{sourceDefinition.Name}' cannot be moved to an unspecified schema. " +
                        "Configure the target schema explicitly or stage a manual migration.");
                }

                renames.Add(new RenameCollationOperation
                {
                    Name = sourceDefinition.Name,
                    Schema = sourceDefinition.Schema,
                    NewName = renamed.Name,
                    NewSchema = renamed.Schema,
                });
                unmatchedTargets.Remove(GetKey(renamed));
            }
            else
            {
                drops.Add(sourceDefinition);
            }
        }

        creates.AddRange(unmatchedTargets.Values);
        var schemas = creates.Select(definition => definition.Schema)
            .Concat(renames.Where(operation =>
                    operation.NewSchema is not null &&
                    !string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
                .Select(operation => operation.NewSchema))
            .Where(schema => schema is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(schema => !baseOperations.OfType<EnsureSchemaOperation>()
                .Any(operation => string.Equals(operation.Name, schema, StringComparison.Ordinal)));
        foreach (var schema in schemas)
        {
            beforeObjects.Add(new EnsureSchemaOperation { Name = schema });
        }

        foreach (var operation in renames
                     .OrderBy(operation => operation.NewSchema, StringComparer.Ordinal)
                     .ThenBy(operation => operation.NewName, StringComparer.Ordinal))
        {
            beforeObjects.Add(operation);
        }

        foreach (var definition in creates
                     .OrderBy(definition => definition.Schema, StringComparer.Ordinal)
                     .ThenBy(definition => definition.Name, StringComparer.Ordinal))
        {
            beforeObjects.Add(new CreateCollationOperation { Definition = definition });
        }

        foreach (var definition in drops
                     .OrderByDescending(definition => definition.Schema, StringComparer.Ordinal)
                     .ThenByDescending(definition => definition.Name, StringComparer.Ordinal))
        {
            afterObjects.Add(new DropCollationOperation
            {
                Name = definition.Name,
                Schema = definition.Schema,
                IsDestructiveChange = true,
            });
        }
    }

    private static BlueTuskCollationDefinitionSet GetDefinitions(IRelationalModel? model) =>
        model is null ? BlueTuskCollationDefinitionSet.Empty : BlueTuskCollationMetadata.Get(model.Model);

    private static CollationKey GetKey(BlueTuskCollationDefinition definition) =>
        new(definition.Schema, definition.Name);

    private static bool BodyEquals(
        BlueTuskCollationDefinition left,
        BlueTuskCollationDefinition right) =>
        string.Equals(
            BlueTuskCollationMetadata.Serialize(left with { Name = "_", Schema = null }),
            BlueTuskCollationMetadata.Serialize(right with { Name = "_", Schema = null }),
            StringComparison.Ordinal);

    private readonly record struct CollationKey(string? Schema, string Name);
}

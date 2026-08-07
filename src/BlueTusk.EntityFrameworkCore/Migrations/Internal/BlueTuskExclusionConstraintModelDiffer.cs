using BlueTusk.EntityFrameworkCore.ExclusionConstraints;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskExclusionConstraintModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskExclusionConstraintMetadata.Serialize(GetTables(source)),
            BlueTuskExclusionConstraintMetadata.Serialize(GetTables(target)),
            StringComparison.Ordinal);

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        IReadOnlyList<MigrationOperation> baseOperations,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var source = GetTables(sourceModel).ToDictionary(TableKey.Create);
        var target = GetTables(targetModel).ToDictionary(TableKey.Create);
        var targetRelationalTables = targetModel?.Tables
            .Select(table => new TableKey(table.Schema, table.Name))
            .ToHashSet() ?? [];
        var processedTargets = new HashSet<TableKey>();

        foreach (var (sourceKey, sourceTable) in source)
        {
            var targetKey = ResolveTargetKey(sourceKey, baseOperations);
            var targetExists = targetRelationalTables.Contains(targetKey);
            if (!targetExists && targetRelationalTables.Contains(sourceKey))
            {
                targetKey = sourceKey;
                targetExists = true;
            }

            if (!targetExists)
            {
                continue;
            }

            var targetConstraints = target.TryGetValue(targetKey, out var targetTable)
                ? targetTable.Constraints
                : [];
            if (targetTable is not null)
            {
                processedTargets.Add(targetKey);
            }

            AddConstraintDifferences(
                sourceKey,
                targetKey,
                sourceTable.Constraints,
                targetConstraints,
                before,
                after);
        }

        foreach (var (targetKey, targetTable) in target)
        {
            if (processedTargets.Contains(targetKey))
            {
                continue;
            }

            foreach (var constraint in targetTable.Constraints)
            {
                after.Add(CreateAdd(targetKey, constraint));
            }
        }
    }

    private static void AddConstraintDifferences(
        TableKey sourceTable,
        TableKey targetTable,
        IReadOnlyList<BlueTuskExclusionConstraintDefinition> source,
        IReadOnlyList<BlueTuskExclusionConstraintDefinition> target,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var sourceByName = source.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var targetByName = target.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var unmatchedTargets = new Dictionary<string, BlueTuskExclusionConstraintDefinition>(
            targetByName,
            StringComparer.Ordinal);

        foreach (var (name, sourceConstraint) in sourceByName)
        {
            if (targetByName.TryGetValue(name, out var targetConstraint))
            {
                unmatchedTargets.Remove(name);
                if (!DefinitionEquals(sourceConstraint, targetConstraint))
                {
                    before.Add(CreateDrop(sourceTable, name));
                    after.Add(CreateAdd(targetTable, targetConstraint));
                }

                continue;
            }

            var renameCandidates = unmatchedTargets.Values
                .Where(candidate => BodyEquals(sourceConstraint, candidate))
                .ToArray();
            if (renameCandidates.Length == 1)
            {
                var renamed = renameCandidates[0];
                after.Add(new RenameExclusionConstraintOperation
                {
                    Table = targetTable.Name,
                    Schema = targetTable.Schema,
                    Name = sourceConstraint.Name,
                    NewName = renamed.Name,
                });
                unmatchedTargets.Remove(renamed.Name);
            }
            else
            {
                before.Add(CreateDrop(sourceTable, sourceConstraint.Name));
            }
        }

        foreach (var constraint in unmatchedTargets.Values)
        {
            after.Add(CreateAdd(targetTable, constraint));
        }
    }

    private static TableKey ResolveTargetKey(
        TableKey source,
        IReadOnlyList<MigrationOperation> baseOperations)
    {
        var rename = baseOperations.OfType<RenameTableOperation>().SingleOrDefault(
            operation =>
                string.Equals(operation.Name, source.Name, StringComparison.Ordinal) &&
                string.Equals(operation.Schema, source.Schema, StringComparison.Ordinal));
        return rename is null
            ? source
            : new TableKey(rename.NewSchema ?? rename.Schema, rename.NewName ?? rename.Name);
    }

    private static AddExclusionConstraintOperation CreateAdd(
        TableKey table,
        BlueTuskExclusionConstraintDefinition definition) =>
        new()
        {
            Table = table.Name,
            Schema = table.Schema,
            Definition = definition,
        };

    private static DropExclusionConstraintOperation CreateDrop(TableKey table, string name) =>
        new()
        {
            Table = table.Name,
            Schema = table.Schema,
            Name = name,
            IsDestructiveChange = true,
        };

    private static bool DefinitionEquals(
        BlueTuskExclusionConstraintDefinition left,
        BlueTuskExclusionConstraintDefinition right) =>
        string.Equals(
            BlueTuskExclusionConstraintMetadata.Serialize(left),
            BlueTuskExclusionConstraintMetadata.Serialize(right),
            StringComparison.Ordinal);

    private static bool BodyEquals(
        BlueTuskExclusionConstraintDefinition left,
        BlueTuskExclusionConstraintDefinition right) =>
        DefinitionEquals(left with { Name = "_" }, right with { Name = "_" });

    private static IReadOnlyList<BlueTuskExclusionConstraintTableDefinition> GetTables(
        IRelationalModel? model) =>
        BlueTuskExclusionConstraintMetadata.GetTables(model);

    private readonly record struct TableKey(string? Schema, string Name)
    {
        public static TableKey Create(BlueTuskExclusionConstraintTableDefinition definition) =>
            new(definition.Schema, definition.Name);
    }
}

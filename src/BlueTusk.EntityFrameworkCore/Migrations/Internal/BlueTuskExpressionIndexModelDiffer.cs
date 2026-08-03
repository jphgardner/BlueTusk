using BlueTusk.EntityFrameworkCore.ExpressionIndexes;
using BlueTusk.EntityFrameworkCore.ExpressionIndexes.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskExpressionIndexModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskExpressionIndexMetadata.Serialize(GetTables(source)),
            BlueTuskExpressionIndexMetadata.Serialize(GetTables(target)),
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

            var targetIndexes = target.TryGetValue(targetKey, out var targetTable)
                ? targetTable.Indexes
                : [];
            if (targetTable is not null)
            {
                processedTargets.Add(targetKey);
            }

            AddIndexDifferences(
                sourceKey,
                targetKey,
                sourceTable.Indexes,
                targetIndexes,
                before,
                after);
        }

        foreach (var (targetKey, targetTable) in target)
        {
            if (processedTargets.Contains(targetKey))
            {
                continue;
            }

            foreach (var index in targetTable.Indexes)
            {
                after.Add(Create(targetKey, index));
            }
        }
    }

    private static void AddIndexDifferences(
        TableKey sourceTable,
        TableKey targetTable,
        IReadOnlyList<BlueTuskExpressionIndexDefinition> source,
        IReadOnlyList<BlueTuskExpressionIndexDefinition> target,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var targetByName = target.ToDictionary(index => index.Name, StringComparer.Ordinal);
        var unmatchedTargets = new Dictionary<string, BlueTuskExpressionIndexDefinition>(
            targetByName,
            StringComparer.Ordinal);
        foreach (var sourceIndex in source)
        {
            if (targetByName.TryGetValue(sourceIndex.Name, out var targetIndex))
            {
                unmatchedTargets.Remove(sourceIndex.Name);
                if (!DefinitionEquals(sourceIndex, targetIndex))
                {
                    before.Add(Drop(sourceTable, sourceIndex));
                    after.Add(Create(targetTable, targetIndex));
                }

                continue;
            }

            var renameCandidates = unmatchedTargets.Values.Where(candidate => BodyEquals(sourceIndex, candidate))
                .ToArray();
            if (renameCandidates.Length == 1)
            {
                var renamed = renameCandidates[0];
                after.Add(new RenameBlueTuskExpressionIndexOperation
                {
                    Name = sourceIndex.Name,
                    Schema = targetTable.Schema,
                    NewName = renamed.Name,
                });
                unmatchedTargets.Remove(renamed.Name);
            }
            else
            {
                before.Add(Drop(sourceTable, sourceIndex));
            }
        }

        foreach (var targetIndex in unmatchedTargets.Values)
        {
            after.Add(Create(targetTable, targetIndex));
        }
    }

    private static TableKey ResolveTargetKey(TableKey source, IReadOnlyList<MigrationOperation> baseOperations)
    {
        var rename = baseOperations.OfType<RenameTableOperation>().SingleOrDefault(operation =>
            string.Equals(operation.Name, source.Name, StringComparison.Ordinal) &&
            string.Equals(operation.Schema, source.Schema, StringComparison.Ordinal));
        return rename is null
            ? source
            : new TableKey(rename.NewSchema ?? rename.Schema, rename.NewName ?? rename.Name);
    }

    private static CreateBlueTuskExpressionIndexOperation Create(
        TableKey table,
        BlueTuskExpressionIndexDefinition definition) =>
        new()
        {
            Table = table.Name,
            Schema = table.Schema,
            Definition = definition,
        };

    private static DropBlueTuskExpressionIndexOperation Drop(
        TableKey table,
        BlueTuskExpressionIndexDefinition definition) =>
        new()
        {
            Name = definition.Name,
            Schema = table.Schema,
            Concurrently = definition.IsConcurrent,
            IsDestructiveChange = true,
        };

    private static bool DefinitionEquals(
        BlueTuskExpressionIndexDefinition left,
        BlueTuskExpressionIndexDefinition right) =>
        string.Equals(
            BlueTuskExpressionIndexMetadata.Serialize(left),
            BlueTuskExpressionIndexMetadata.Serialize(right),
            StringComparison.Ordinal);

    private static bool BodyEquals(
        BlueTuskExpressionIndexDefinition left,
        BlueTuskExpressionIndexDefinition right) =>
        DefinitionEquals(left with { Name = "_" }, right with { Name = "_" });

    private static IReadOnlyList<BlueTuskExpressionIndexTableDefinition> GetTables(IRelationalModel? model) =>
        BlueTuskExpressionIndexMetadata.GetTables(model);

    private readonly record struct TableKey(string? Schema, string Name)
    {
        public static TableKey Create(BlueTuskExpressionIndexTableDefinition definition) =>
            new(definition.Schema, definition.Name);
    }
}

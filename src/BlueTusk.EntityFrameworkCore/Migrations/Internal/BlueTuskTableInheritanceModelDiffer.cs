using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.TableInheritance;
using BlueTusk.EntityFrameworkCore.TableInheritance.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskTableInheritanceModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskTableInheritanceMetadata.Serialize(GetTables(source)),
            BlueTuskTableInheritanceMetadata.Serialize(GetTables(target)),
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
        var renames = baseOperations.OfType<RenameTableOperation>()
            .ToDictionary(
                operation => new TableKey(operation.Schema, operation.Name),
                operation => new TableKey(
                    operation.NewSchema ?? operation.Schema,
                    operation.NewName ?? operation.Name));
        var processedTargets = new HashSet<TableKey>();

        foreach (var (sourceKey, sourceTable) in source)
        {
            var targetKey = renames.GetValueOrDefault(sourceKey, sourceKey);
            target.TryGetValue(targetKey, out var targetTable);
            if (targetTable is not null)
            {
                processedTargets.Add(targetKey);
            }

            AddTableDifferences(
                sourceKey,
                targetKey,
                sourceTable,
                targetTable,
                renames,
                before,
                after);
        }

        foreach (var (targetKey, targetTable) in target)
        {
            if (processedTargets.Contains(targetKey))
            {
                continue;
            }

            foreach (var parent in targetTable.Inheritance.Parents)
            {
                after.Add(CreateAdd(targetKey, TableKey.Create(parent)));
            }
        }
    }

    private static void AddTableDifferences(
        TableKey sourceKey,
        TableKey targetKey,
        BlueTuskTableInheritanceTableDefinition source,
        BlueTuskTableInheritanceTableDefinition? target,
        IReadOnlyDictionary<TableKey, TableKey> renames,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var sourceParents = source.Inheritance.Parents
            .Select(parent => new ParentChange(TableKey.Create(parent), Rename(TableKey.Create(parent), renames)))
            .ToArray();
        var targetParents = target?.Inheritance.Parents.Select(TableKey.Create).ToArray() ?? [];
        var mappedSource = sourceParents.Select(parent => parent.Target).ToArray();
        if (mappedSource.SequenceEqual(targetParents))
        {
            return;
        }

        var targetSet = targetParents.ToHashSet();
        var sourceSet = mappedSource.ToHashSet();
        var retained = mappedSource.Where(targetSet.Contains).ToArray();
        var targetRetained = targetParents.Where(sourceSet.Contains).ToArray();
        var canModifyInPlace = retained.SequenceEqual(targetRetained) &&
            targetParents.Take(retained.Length).SequenceEqual(retained);

        if (!canModifyInPlace)
        {
            foreach (var parent in sourceParents.Reverse())
            {
                before.Add(CreateRemove(sourceKey, parent.Source));
            }

            foreach (var parent in targetParents)
            {
                after.Add(CreateAdd(targetKey, parent));
            }

            return;
        }

        foreach (var parent in sourceParents.Where(parent => !targetSet.Contains(parent.Target)).Reverse())
        {
            before.Add(CreateRemove(sourceKey, parent.Source));
        }

        foreach (var parent in targetParents.Where(parent => !sourceSet.Contains(parent)))
        {
            after.Add(CreateAdd(targetKey, parent));
        }
    }

    private static TableKey Rename(TableKey key, IReadOnlyDictionary<TableKey, TableKey> renames) =>
        renames.GetValueOrDefault(key, key);

    private static AddTableInheritanceOperation CreateAdd(TableKey table, TableKey parent) =>
        new()
        {
            Table = table.Name,
            Schema = table.Schema,
            ParentTable = parent.Name,
            ParentSchema = parent.Schema,
        };

    private static RemoveTableInheritanceOperation CreateRemove(TableKey table, TableKey parent) =>
        new()
        {
            Table = table.Name,
            Schema = table.Schema,
            ParentTable = parent.Name,
            ParentSchema = parent.Schema,
        };

    private static IReadOnlyList<BlueTuskTableInheritanceTableDefinition> GetTables(
        IRelationalModel? model) =>
        BlueTuskTableInheritanceMetadata.GetTables(model);

    private sealed record ParentChange(TableKey Source, TableKey Target);

    private readonly record struct TableKey(string? Schema, string Name)
    {
        public static TableKey Create(BlueTuskTableInheritanceTableDefinition definition) =>
            new(definition.Schema, definition.Name);

        public static TableKey Create(BlueTuskInheritedTableDefinition definition) =>
            new(definition.Schema, definition.Name);
    }
}

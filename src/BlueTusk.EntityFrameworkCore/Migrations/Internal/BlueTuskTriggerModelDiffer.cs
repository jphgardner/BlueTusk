using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Triggers;
using BlueTusk.EntityFrameworkCore.Triggers.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskTriggerModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskTriggerMetadata.Serialize(BlueTuskTriggerMetadata.GetTables(source)),
            BlueTuskTriggerMetadata.Serialize(BlueTuskTriggerMetadata.GetTables(target)),
            StringComparison.Ordinal);

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        IReadOnlyList<MigrationOperation> baseOperations,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var source = BlueTuskTriggerMetadata.GetTables(sourceModel).ToDictionary(TableKey.Create);
        var target = BlueTuskTriggerMetadata.GetTables(targetModel).ToDictionary(TableKey.Create);
        var targetRelations = GetTargetRelations(targetModel);
        var processedTargets = new HashSet<TableKey>();

        foreach (var (sourceKey, sourceTable) in source)
        {
            var targetKey = ResolveTargetKey(sourceKey, baseOperations);
            if (!targetRelations.Contains(targetKey) && targetRelations.Contains(sourceKey))
            {
                targetKey = sourceKey;
            }

            if (!targetRelations.Contains(targetKey))
            {
                continue;
            }

            var targetTriggers = target.TryGetValue(targetKey, out var targetTable)
                ? targetTable.Triggers
                : [];
            if (targetTable is not null)
            {
                processedTargets.Add(targetKey);
            }

            AddTriggerDifferences(sourceKey, targetKey, sourceTable.Triggers, targetTriggers, before, after);
        }

        foreach (var (targetKey, targetTable) in target)
        {
            if (processedTargets.Contains(targetKey))
            {
                continue;
            }

            foreach (var trigger in targetTable.Triggers)
            {
                after.Add(Create(targetKey, trigger));
            }
        }
    }

    private static void AddTriggerDifferences(
        TableKey sourceTable,
        TableKey targetTable,
        IReadOnlyList<BlueTuskTriggerDefinition> source,
        IReadOnlyList<BlueTuskTriggerDefinition> target,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var targetByName = target.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var unmatched = new Dictionary<string, BlueTuskTriggerDefinition>(targetByName, StringComparer.Ordinal);
        foreach (var sourceTrigger in source)
        {
            if (targetByName.TryGetValue(sourceTrigger.Name, out var targetTrigger))
            {
                unmatched.Remove(sourceTrigger.Name);
                if (!BodyEquals(sourceTrigger, targetTrigger))
                {
                    before.Add(Drop(sourceTable, sourceTrigger.Name));
                    after.Add(Create(targetTable, targetTrigger));
                }
                else if (sourceTrigger.EnabledMode != targetTrigger.EnabledMode)
                {
                    after.Add(AlterMode(targetTable, targetTrigger));
                }

                continue;
            }

            var candidates = unmatched.Values.Where(item => BodyEquals(sourceTrigger, item)).ToArray();
            if (candidates.Length == 1)
            {
                var renamed = candidates[0];
                after.Add(new RenameTriggerOperation
                {
                    Table = targetTable.Name,
                    Schema = targetTable.Schema,
                    Name = sourceTrigger.Name,
                    NewName = renamed.Name,
                });
                if (sourceTrigger.EnabledMode != renamed.EnabledMode)
                {
                    after.Add(AlterMode(targetTable, renamed));
                }

                unmatched.Remove(renamed.Name);
            }
            else
            {
                before.Add(Drop(sourceTable, sourceTrigger.Name));
            }
        }

        foreach (var trigger in unmatched.Values)
        {
            after.Add(Create(targetTable, trigger));
        }
    }

    private static HashSet<TableKey> GetTargetRelations(IRelationalModel? model)
    {
        if (model is null)
        {
            return [];
        }

        var result = model.Tables.Select(table => new TableKey(table.Schema, table.Name)).ToHashSet();
        foreach (var entityType in model.Model.GetEntityTypes())
        {
            if (entityType.GetViewName() is { } viewName)
            {
                result.Add(new TableKey(entityType.GetViewSchema(), viewName));
            }
        }

        return result;
    }

    private static TableKey ResolveTargetKey(TableKey source, IReadOnlyList<MigrationOperation> operations)
    {
        var rename = operations.OfType<RenameTableOperation>().SingleOrDefault(operation =>
            string.Equals(operation.Name, source.Name, StringComparison.Ordinal) &&
            string.Equals(operation.Schema, source.Schema, StringComparison.Ordinal));
        return rename is null
            ? source
            : new TableKey(rename.NewSchema ?? rename.Schema, rename.NewName ?? rename.Name);
    }

    private static CreateTriggerOperation Create(TableKey table, BlueTuskTriggerDefinition definition) =>
        new() { Table = table.Name, Schema = table.Schema, Definition = definition };

    private static DropTriggerOperation Drop(TableKey table, string name) =>
        new() { Table = table.Name, Schema = table.Schema, Name = name, IsDestructiveChange = true };

    private static AlterTriggerEnabledModeOperation AlterMode(
        TableKey table,
        BlueTuskTriggerDefinition definition) =>
        new()
        {
            Table = table.Name,
            Schema = table.Schema,
            Name = definition.Name,
            EnabledMode = definition.EnabledMode,
        };

    private static bool BodyEquals(BlueTuskTriggerDefinition left, BlueTuskTriggerDefinition right) =>
        string.Equals(
            BlueTuskTriggerMetadata.Serialize(left with { Name = "_", EnabledMode = BlueTuskTriggerEnabledMode.Origin }),
            BlueTuskTriggerMetadata.Serialize(right with { Name = "_", EnabledMode = BlueTuskTriggerEnabledMode.Origin }),
            StringComparison.Ordinal);

    private readonly record struct TableKey(string? Schema, string Name)
    {
        public static TableKey Create(BlueTuskTriggerTableDefinition definition) =>
            new(definition.Schema, definition.Name);
    }
}

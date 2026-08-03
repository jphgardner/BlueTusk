using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskRowLevelSecurityModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskRowLevelSecurityMetadata.Serialize(GetTables(source)),
            BlueTuskRowLevelSecurityMetadata.Serialize(GetTables(target)),
            StringComparison.Ordinal);

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        IReadOnlyList<MigrationOperation> baseOperations,
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

            var targetDefinition = target.TryGetValue(targetKey, out var targetTable)
                ? targetTable.RowLevelSecurity
                : new BlueTuskRowLevelSecurityDefinition(false, false, []);
            if (targetTable is not null)
            {
                processedTargets.Add(targetKey);
            }

            AddPolicyDifferences(
                targetKey,
                sourceTable.RowLevelSecurity.Policies,
                targetDefinition.Policies,
                after);
            AddSettingDifferences(
                targetKey,
                sourceTable.RowLevelSecurity,
                targetDefinition,
                after);
        }

        foreach (var (targetKey, targetTable) in target)
        {
            if (processedTargets.Contains(targetKey))
            {
                continue;
            }

            foreach (var policy in targetTable.RowLevelSecurity.Policies)
            {
                after.Add(CreatePolicy(targetKey, policy));
            }

            var definition = targetTable.RowLevelSecurity;
            if (definition.Enabled || definition.Forced)
            {
                after.Add(new AlterRowLevelSecurityOperation
                {
                    Table = targetKey.Name,
                    Schema = targetKey.Schema,
                    Enabled = definition.Enabled ? true : null,
                    Forced = definition.Forced ? true : null,
                });
            }
        }
    }

    private static void AddPolicyDifferences(
        TableKey table,
        IReadOnlyList<BlueTuskRowSecurityPolicyDefinition> source,
        IReadOnlyList<BlueTuskRowSecurityPolicyDefinition> target,
        ICollection<MigrationOperation> operations)
    {
        var sourceByName = source.ToDictionary(policy => policy.Name, StringComparer.Ordinal);
        var targetByName = target.ToDictionary(policy => policy.Name, StringComparer.Ordinal);
        var unmatchedTargets = new Dictionary<string, BlueTuskRowSecurityPolicyDefinition>(
            targetByName,
            StringComparer.Ordinal);

        foreach (var (name, sourcePolicy) in sourceByName)
        {
            if (targetByName.TryGetValue(name, out var targetPolicy))
            {
                unmatchedTargets.Remove(name);
                if (!PolicyEquals(sourcePolicy, targetPolicy))
                {
                    if (CanAlter(sourcePolicy, targetPolicy))
                    {
                        operations.Add(new AlterRowSecurityPolicyOperation
                        {
                            Table = table.Name,
                            Schema = table.Schema,
                            Definition = targetPolicy,
                        });
                    }
                    else
                    {
                        operations.Add(DropPolicy(table, name));
                        operations.Add(CreatePolicy(table, targetPolicy));
                    }
                }

                continue;
            }

            var renameCandidates = unmatchedTargets.Values
                .Where(candidate => PolicyBodyEquals(sourcePolicy, candidate))
                .ToArray();
            if (renameCandidates.Length == 1)
            {
                var renamed = renameCandidates[0];
                operations.Add(new RenameRowSecurityPolicyOperation
                {
                    Table = table.Name,
                    Schema = table.Schema,
                    Name = sourcePolicy.Name,
                    NewName = renamed.Name,
                });
                unmatchedTargets.Remove(renamed.Name);
            }
            else
            {
                operations.Add(DropPolicy(table, sourcePolicy.Name));
            }
        }

        foreach (var policy in unmatchedTargets.Values)
        {
            operations.Add(CreatePolicy(table, policy));
        }
    }

    private static void AddSettingDifferences(
        TableKey table,
        BlueTuskRowLevelSecurityDefinition source,
        BlueTuskRowLevelSecurityDefinition target,
        ICollection<MigrationOperation> operations)
    {
        if (source.Enabled == target.Enabled && source.Forced == target.Forced)
        {
            return;
        }

        operations.Add(new AlterRowLevelSecurityOperation
        {
            Table = table.Name,
            Schema = table.Schema,
            Enabled = source.Enabled == target.Enabled ? null : target.Enabled,
            Forced = source.Forced == target.Forced ? null : target.Forced,
        });
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

    private static CreateRowSecurityPolicyOperation CreatePolicy(
        TableKey table,
        BlueTuskRowSecurityPolicyDefinition policy) =>
        new()
        {
            Table = table.Name,
            Schema = table.Schema,
            Definition = policy,
        };

    private static DropRowSecurityPolicyOperation DropPolicy(TableKey table, string name) =>
        new()
        {
            Table = table.Name,
            Schema = table.Schema,
            Name = name,
        };

    private static bool PolicyEquals(
        BlueTuskRowSecurityPolicyDefinition left,
        BlueTuskRowSecurityPolicyDefinition right) =>
        string.Equals(
            BlueTuskRowLevelSecurityMetadata.Serialize(left),
            BlueTuskRowLevelSecurityMetadata.Serialize(right),
            StringComparison.Ordinal);

    private static bool PolicyBodyEquals(
        BlueTuskRowSecurityPolicyDefinition left,
        BlueTuskRowSecurityPolicyDefinition right) =>
        PolicyEquals(left with { Name = string.Empty }, right with { Name = string.Empty });

    private static bool CanAlter(
        BlueTuskRowSecurityPolicyDefinition source,
        BlueTuskRowSecurityPolicyDefinition target) =>
        source.Behavior == target.Behavior &&
        source.Command == target.Command &&
        (source.UsingSql is null || target.UsingSql is not null) &&
        (source.WithCheckSql is null || target.WithCheckSql is not null);

    private static IReadOnlyList<BlueTuskRowLevelSecurityTableDefinition> GetTables(
        IRelationalModel? model) =>
        BlueTuskRowLevelSecurityMetadata.GetTables(model);

    private readonly record struct TableKey(string? Schema, string Name)
    {
        public static TableKey Create(BlueTuskRowLevelSecurityTableDefinition definition) =>
            new(definition.Schema, definition.Name);
    }
}

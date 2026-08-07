using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Rules;
using BlueTusk.EntityFrameworkCore.Rules.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskRuleModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskRuleMetadata.Serialize(BlueTuskRuleMetadata.GetTables(source)),
            BlueTuskRuleMetadata.Serialize(BlueTuskRuleMetadata.GetTables(target)),
            StringComparison.Ordinal);

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        IReadOnlyList<MigrationOperation> baseOperations,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var source = BlueTuskRuleMetadata.GetTables(sourceModel).ToDictionary(Key.Create);
        var target = BlueTuskRuleMetadata.GetTables(targetModel).ToDictionary(Key.Create);
        var targetRelations = GetRelations(targetModel);
        var processed = new HashSet<Key>();
        foreach (var (sourceKey, sourceTable) in source)
        {
            var targetKey = ResolveTarget(sourceKey, baseOperations);
            if (!targetRelations.Contains(targetKey) && targetRelations.Contains(sourceKey))
            {
                targetKey = sourceKey;
            }

            if (!targetRelations.Contains(targetKey))
            {
                continue;
            }

            var targetRules = target.TryGetValue(targetKey, out var targetTable) ? targetTable.Rules : [];
            if (targetTable is not null)
            {
                processed.Add(targetKey);
            }

            Diff(sourceKey, targetKey, sourceTable.Rules, targetRules, before, after);
        }

        foreach (var (key, table) in target.Where(item => !processed.Contains(item.Key)))
        {
            foreach (var rule in table.Rules)
            {
                after.Add(Create(key, rule));
            }
        }
    }

    private static void Diff(
        Key sourceTable,
        Key targetTable,
        IReadOnlyList<BlueTuskRuleDefinition> source,
        IReadOnlyList<BlueTuskRuleDefinition> target,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var targetByName = target.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var unmatched = new Dictionary<string, BlueTuskRuleDefinition>(targetByName, StringComparer.Ordinal);
        foreach (var oldRule in source)
        {
            if (targetByName.TryGetValue(oldRule.Name, out var newRule))
            {
                unmatched.Remove(oldRule.Name);
                if (!BodyEquals(oldRule, newRule))
                {
                    after.Add(Create(targetTable, newRule, orReplace: true));
                }
                else if (oldRule.EnabledMode != newRule.EnabledMode)
                {
                    after.Add(AlterMode(targetTable, newRule));
                }

                continue;
            }

            var candidates = unmatched.Values.Where(item => BodyEquals(oldRule, item)).ToArray();
            if (candidates.Length == 1)
            {
                var renamed = candidates[0];
                after.Add(new RenameRuleOperation
                {
                    Table = targetTable.Name,
                    Schema = targetTable.Schema,
                    Name = oldRule.Name,
                    NewName = renamed.Name,
                });
                if (oldRule.EnabledMode != renamed.EnabledMode)
                {
                    after.Add(AlterMode(targetTable, renamed));
                }

                unmatched.Remove(renamed.Name);
            }
            else
            {
                before.Add(Drop(sourceTable, oldRule.Name));
            }
        }

        foreach (var rule in unmatched.Values)
        {
            after.Add(Create(targetTable, rule));
        }
    }

    private static HashSet<Key> GetRelations(IRelationalModel? model)
    {
        if (model is null)
        {
            return [];
        }

        var result = model.Tables.Select(table => new Key(table.Schema, table.Name)).ToHashSet();
        foreach (var entityType in model.Model.GetEntityTypes())
        {
            if (entityType.GetViewName() is { } view)
            {
                result.Add(new Key(entityType.GetViewSchema(), view));
            }
        }

        return result;
    }

    private static Key ResolveTarget(Key source, IReadOnlyList<MigrationOperation> operations)
    {
        var rename = operations.OfType<RenameTableOperation>().SingleOrDefault(item =>
            item.Name == source.Name && item.Schema == source.Schema);
        return rename is null ? source : new Key(rename.NewSchema ?? rename.Schema, rename.NewName ?? rename.Name);
    }

    private static CreateRuleOperation Create(Key key, BlueTuskRuleDefinition rule, bool orReplace = false) =>
        new() { Table = key.Name, Schema = key.Schema, Definition = rule, OrReplace = orReplace };

    private static DropRuleOperation Drop(Key key, string name) =>
        new() { Table = key.Name, Schema = key.Schema, Name = name, IsDestructiveChange = true };

    private static AlterRuleEnabledModeOperation AlterMode(Key key, BlueTuskRuleDefinition rule) =>
        new() { Table = key.Name, Schema = key.Schema, Name = rule.Name, EnabledMode = rule.EnabledMode };

    private static bool BodyEquals(BlueTuskRuleDefinition left, BlueTuskRuleDefinition right) =>
        BlueTuskRuleMetadata.Serialize(NormalizeBody(left)) ==
        BlueTuskRuleMetadata.Serialize(NormalizeBody(right));

    private static BlueTuskRuleDefinition NormalizeBody(BlueTuskRuleDefinition definition) =>
        definition with
        {
            Name = "_",
            EnabledMode = BlueTuskRuleEnabledMode.Origin,
            CanonicalCreateSql = NormalizeCanonicalName(definition.CanonicalCreateSql),
        };

    private static string? NormalizeCanonicalName(string? canonicalSql)
    {
        if (canonicalSql is null)
        {
            return null;
        }

        const string prefix = "CREATE RULE ";
        var sql = canonicalSql.Trim();
        if (!sql.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return sql;
        }

        var index = prefix.Length;
        if (index < sql.Length && sql[index] == '"')
        {
            for (index++; index < sql.Length; index++)
            {
                if (sql[index] != '"')
                {
                    continue;
                }

                if (index + 1 < sql.Length && sql[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                index++;
                break;
            }
        }
        else
        {
            while (index < sql.Length && !char.IsWhiteSpace(sql[index]))
            {
                index++;
            }
        }

        return "CREATE RULE _" + sql[index..];
    }

    private readonly record struct Key(string? Schema, string Name)
    {
        public static Key Create(BlueTuskRuleTableDefinition definition) => new(definition.Schema, definition.Name);
    }
}

using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Views;
using BlueTusk.EntityFrameworkCore.Views.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskViewModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskViewMetadata.Serialize(GetDefinitions(source)),
            BlueTuskViewMetadata.Serialize(GetDefinitions(target)),
            StringComparison.Ordinal);

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        IReadOnlyList<MigrationOperation> baseOperations,
        ICollection<MigrationOperation> beforeRelational,
        ICollection<MigrationOperation> afterRelational)
    {
        var source = Entries(GetDefinitions(sourceModel)).ToDictionary(entry => entry.Key);
        var target = Entries(GetDefinitions(targetModel)).ToDictionary(entry => entry.Key);
        RejectKindChanges(source, target);

        var forcedReplacements = FindForcedReplacementClosure(source, target);
        var drops = new Dictionary<BlueTuskViewMetadata.ViewKey, ViewEntry>();
        var creates = new Dictionary<BlueTuskViewMetadata.ViewKey, ViewEntry>();
        var after = new List<(ViewEntry Entry, MigrationOperation Operation)>();
        var processedTargets = new HashSet<BlueTuskViewMetadata.ViewKey>();

        foreach (var oldEntry in source.Values)
        {
            if (forcedReplacements.Contains(oldEntry.Key))
            {
                drops[oldEntry.Key] = oldEntry;
                if (target.TryGetValue(oldEntry.Key, out var replacement))
                {
                    creates[replacement.Key] = replacement;
                    processedTargets.Add(replacement.Key);
                }

                continue;
            }

            if (target.TryGetValue(oldEntry.Key, out var entry))
            {
                processedTargets.Add(entry.Key);
                AddAlteration(oldEntry, entry, after);
                continue;
            }

            var renameCandidates = target.Values
                .Where(candidate => !processedTargets.Contains(candidate.Key) &&
                                    candidate.Kind == oldEntry.Kind &&
                                    !source.ContainsKey(candidate.Key) &&
                                    BodyEquals(oldEntry, candidate))
                .ToArray();
            if (renameCandidates.Length == 1)
            {
                var renamed = renameCandidates[0];
                processedTargets.Add(renamed.Key);
                after.Add((renamed, new RenameBlueTuskViewOperation
                {
                    Kind = oldEntry.Kind,
                    Name = oldEntry.Name,
                    Schema = oldEntry.Schema,
                    NewName = renamed.Name,
                    NewSchema = renamed.Schema,
                }));
            }
            else
            {
                drops[oldEntry.Key] = oldEntry;
            }
        }

        foreach (var entry in target.Values.Where(entry => !processedTargets.Contains(entry.Key)))
        {
            creates[entry.Key] = entry;
        }

        foreach (var entry in OrderByDependencies(drops.Values, source).AsEnumerable().Reverse())
        {
            beforeRelational.Add(new DropBlueTuskViewOperation
            {
                Kind = entry.Kind,
                Name = entry.Name,
                Schema = entry.Schema,
                IsDestructiveChange = true,
            });
        }

        var ensuredSchemas = baseOperations.OfType<EnsureSchemaOperation>()
            .Select(operation => operation.Name)
            .ToHashSet(StringComparer.Ordinal);
        var orderedAfterEntries = OrderByDependencies(
            creates.Values.Concat(after.Select(item => item.Entry)).DistinctBy(entry => entry.Key),
            target);
        foreach (var schema in creates.Values.Select(entry => entry.Schema)
                     .Concat(after.Select(item => item.Entry.Schema))
                     .Where(schema => schema is not null)
                     .Cast<string>()
                     .Distinct(StringComparer.Ordinal)
                     .Where(ensuredSchemas.Add))
        {
            afterRelational.Add(new EnsureSchemaOperation { Name = schema });
        }

        foreach (var entry in orderedAfterEntries)
        {
            foreach (var operation in after.Where(item => item.Entry.Key == entry.Key)
                         .Select(item => item.Operation))
            {
                afterRelational.Add(operation);
            }

            if (creates.ContainsKey(entry.Key))
            {
                afterRelational.Add(entry.CreateOperation());
            }
        }
    }

    private static void AddAlteration(
        ViewEntry oldEntry,
        ViewEntry entry,
        ICollection<(ViewEntry Entry, MigrationOperation Operation)> after)
    {
        if (BodyEquals(oldEntry, entry))
        {
            return;
        }

        if (entry.Kind == BlueTuskViewKind.View)
        {
            var oldDefinition = (BlueTuskViewDefinition)oldEntry.Definition;
            var definition = (BlueTuskViewDefinition)entry.Definition;
            BlueTuskViewAlterationPlanner.ValidateReplacement(oldDefinition, definition);
            after.Add((entry, new ReplaceBlueTuskViewOperation
            {
                OldDefinition = oldDefinition,
                Definition = definition,
            }));
            return;
        }

        var oldMaterialized = (BlueTuskMaterializedViewDefinition)oldEntry.Definition;
        var materialized = (BlueTuskMaterializedViewDefinition)entry.Definition;
        BlueTuskViewAlterationPlanner.ValidateMaterializedAlteration(oldMaterialized, materialized);
        after.Add((entry, new AlterBlueTuskMaterializedViewOperation
        {
            OldDefinition = oldMaterialized,
            Definition = materialized,
            IsDestructiveChange = oldMaterialized.IsPopulated && !materialized.IsPopulated,
        }));
    }

    private static HashSet<BlueTuskViewMetadata.ViewKey> FindForcedReplacementClosure(
        IReadOnlyDictionary<BlueTuskViewMetadata.ViewKey, ViewEntry> source,
        IReadOnlyDictionary<BlueTuskViewMetadata.ViewKey, ViewEntry> target)
    {
        var forced = source.Values
            .Where(entry => entry.Kind == BlueTuskViewKind.MaterializedView &&
                            target.TryGetValue(entry.Key, out var targetEntry) &&
                            targetEntry.Kind == BlueTuskViewKind.MaterializedView &&
                            MaterializedQueryChanged(entry, targetEntry))
            .Select(entry => entry.Key)
            .ToHashSet();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var entry in source.Values)
            {
                if (!forced.Contains(entry.Key) && entry.Dependencies.Any(forced.Contains))
                {
                    forced.Add(entry.Key);
                    changed = true;
                }
            }
        }

        return forced;
    }

    private static bool MaterializedQueryChanged(ViewEntry source, ViewEntry target)
    {
        var oldDefinition = (BlueTuskMaterializedViewDefinition)source.Definition;
        var definition = (BlueTuskMaterializedViewDefinition)target.Definition;
        return !string.Equals(oldDefinition.QuerySql, definition.QuerySql, StringComparison.Ordinal) ||
               !oldDefinition.Columns.SequenceEqual(definition.Columns, StringComparer.Ordinal);
    }

    private static void RejectKindChanges(
        IReadOnlyDictionary<BlueTuskViewMetadata.ViewKey, ViewEntry> source,
        IReadOnlyDictionary<BlueTuskViewMetadata.ViewKey, ViewEntry> target)
    {
        foreach (var key in source.Keys.Intersect(target.Keys))
        {
            if (source[key].Kind != target[key].Kind)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL relation '{key.Schema}.{key.Name}' cannot change between VIEW and MATERIALIZED VIEW " +
                    "in place. Use an explicit dependency-aware drop/recreate migration.");
            }
        }
    }

    private static List<ViewEntry> OrderByDependencies(
        IEnumerable<ViewEntry> entries,
        IReadOnlyDictionary<BlueTuskViewMetadata.ViewKey, ViewEntry> allEntries)
    {
        var selected = entries.ToDictionary(entry => entry.Key);
        var visited = new HashSet<BlueTuskViewMetadata.ViewKey>();
        var visiting = new HashSet<BlueTuskViewMetadata.ViewKey>();
        var ordered = new List<ViewEntry>();
        foreach (var entry in selected.Values.OrderBy(entry => entry.Schema, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Name, StringComparer.Ordinal))
        {
            Visit(entry);
        }

        return ordered;

        void Visit(ViewEntry entry)
        {
            if (!visited.Add(entry.Key))
            {
                return;
            }

            visiting.Add(entry.Key);
            foreach (var dependency in entry.Dependencies.OrderBy(key => key.Schema, StringComparer.Ordinal)
                         .ThenBy(key => key.Name, StringComparer.Ordinal))
            {
                if (dependency == entry.Key || visiting.Contains(dependency) ||
                    !selected.TryGetValue(dependency, out var selectedDependency) ||
                    !allEntries.ContainsKey(dependency))
                {
                    continue;
                }

                Visit(selectedDependency);
            }

            visiting.Remove(entry.Key);
            ordered.Add(entry);
        }
    }

    private static bool BodyEquals(ViewEntry left, ViewEntry right)
    {
        if (left.Kind != right.Kind)
        {
            return false;
        }

        return left.Kind == BlueTuskViewKind.View
            ? ViewBodyEquals((BlueTuskViewDefinition)left.Definition, (BlueTuskViewDefinition)right.Definition)
            : MaterializedBodyEquals(
                (BlueTuskMaterializedViewDefinition)left.Definition,
                (BlueTuskMaterializedViewDefinition)right.Definition);
    }

    private static bool ViewBodyEquals(BlueTuskViewDefinition left, BlueTuskViewDefinition right) =>
        string.Equals(left.QuerySql, right.QuerySql, StringComparison.Ordinal) &&
        left.Columns.SequenceEqual(right.Columns, StringComparer.Ordinal) &&
        left.SecurityBarrier == right.SecurityBarrier &&
        left.SecurityInvoker == right.SecurityInvoker &&
        left.CheckOption == right.CheckOption &&
        left.IsRecursive == right.IsRecursive;

    private static bool MaterializedBodyEquals(
        BlueTuskMaterializedViewDefinition left,
        BlueTuskMaterializedViewDefinition right) =>
        string.Equals(left.QuerySql, right.QuerySql, StringComparison.Ordinal) &&
        left.Columns.SequenceEqual(right.Columns, StringComparer.Ordinal) &&
        string.Equals(left.AccessMethod, right.AccessMethod, StringComparison.Ordinal) &&
        left.StorageParameters.SequenceEqual(right.StorageParameters) &&
        string.Equals(left.Tablespace, right.Tablespace, StringComparison.Ordinal) &&
        left.IsPopulated == right.IsPopulated;

    private static BlueTuskViewDefinitionSet GetDefinitions(IRelationalModel? model) =>
        model is null ? BlueTuskViewDefinitionSet.Empty : BlueTuskViewMetadata.Get(model.Model);

    private static IEnumerable<ViewEntry> Entries(BlueTuskViewDefinitionSet definitions) =>
        definitions.Views.Select(definition => ViewEntry.Create(definition))
            .Concat(definitions.MaterializedViews.Select(definition => ViewEntry.Create(definition)));

    private sealed record ViewEntry(
        BlueTuskViewKind Kind,
        BlueTuskViewMetadata.ViewKey Key,
        object Definition,
        IReadOnlyList<BlueTuskViewMetadata.ViewKey> Dependencies)
    {
        public string Name => Key.Name;

        public string? Schema => Key.Schema;

        public static ViewEntry Create(BlueTuskViewDefinition definition) => new(
            BlueTuskViewKind.View,
            BlueTuskViewMetadata.ViewKey.Create(definition),
            definition,
            definition.Dependencies.Select(dependency =>
                    new BlueTuskViewMetadata.ViewKey(dependency.Schema, dependency.Name))
                .ToArray());

        public static ViewEntry Create(BlueTuskMaterializedViewDefinition definition) => new(
            BlueTuskViewKind.MaterializedView,
            BlueTuskViewMetadata.ViewKey.Create(definition),
            definition,
            definition.Dependencies.Select(dependency =>
                    new BlueTuskViewMetadata.ViewKey(dependency.Schema, dependency.Name))
                .ToArray());

        public MigrationOperation CreateOperation() => Kind == BlueTuskViewKind.View
            ? new CreateBlueTuskViewOperation { Definition = (BlueTuskViewDefinition)Definition }
            : new CreateBlueTuskMaterializedViewOperation
            {
                Definition = (BlueTuskMaterializedViewDefinition)Definition,
            };
    }
}

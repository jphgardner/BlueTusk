using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Tablespaces;
using BlueTusk.EntityFrameworkCore.Tablespaces.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskTablespaceModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        BlueTuskTablespaceMetadata.Serialize(Get(source)) != BlueTuskTablespaceMetadata.Serialize(Get(target));

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var source = Get(sourceModel).Tablespaces.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var target = Get(targetModel).Tablespaces.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var processedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, oldDefinition) in source)
        {
            if (target.TryGetValue(name, out var definition))
            {
                processedTargets.Add(name);
                if (!BlueTuskTablespaceMetadata.LocationEquals(oldDefinition, definition))
                {
                    throw new InvalidOperationException(
                        $"Tablespace '{name}' cannot change its filesystem location in place. " +
                        "Move every dependent object, then use explicit drop and create operations.");
                }

                AddAlterIfNeeded(oldDefinition, definition, before);
                continue;
            }

            var renameCandidates = target.Values.Where(candidate =>
                    !processedTargets.Contains(candidate.Name) &&
                    !source.ContainsKey(candidate.Name) &&
                    BlueTuskTablespaceMetadata.LocationEquals(oldDefinition, candidate))
                .ToArray();
            if (renameCandidates.Length == 1)
            {
                var renamed = renameCandidates[0];
                processedTargets.Add(renamed.Name);
                before.Add(new RenameBlueTuskTablespaceOperation { Name = name, NewName = renamed.Name });
                AddAlterIfNeeded(oldDefinition with { Name = renamed.Name }, renamed, before);
            }
            else
            {
                after.Add(new DropBlueTuskTablespaceOperation
                {
                    Name = name,
                    IsDestructiveChange = true,
                });
            }
        }

        foreach (var (name, definition) in target)
        {
            if (!source.ContainsKey(name) && !processedTargets.Contains(name))
            {
                before.Add(new CreateBlueTuskTablespaceOperation { Definition = definition });
            }
        }
    }

    private static void AddAlterIfNeeded(
        BlueTuskTablespaceDefinition oldDefinition,
        BlueTuskTablespaceDefinition definition,
        ICollection<MigrationOperation> operations)
    {
        if (BlueTuskTablespaceMetadata.Serialize(oldDefinition) !=
            BlueTuskTablespaceMetadata.Serialize(definition))
        {
            operations.Add(new AlterBlueTuskTablespaceOperation
            {
                OldDefinition = oldDefinition,
                Definition = definition,
            });
        }
    }

    private static BlueTuskTablespaceDefinitionSet Get(IRelationalModel? model) =>
        model is null ? BlueTuskTablespaceDefinitionSet.Empty : BlueTuskTablespaceMetadata.Get(model.Model);
}

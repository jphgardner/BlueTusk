using BlueTusk.EntityFrameworkCore.EventTriggers;
using BlueTusk.EntityFrameworkCore.EventTriggers.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskEventTriggerModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        BlueTuskEventTriggerMetadata.Serialize(Get(source)) != BlueTuskEventTriggerMetadata.Serialize(Get(target));

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var source = Get(sourceModel).EventTriggers.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var target = Get(targetModel).EventTriggers.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var processedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, oldDefinition) in source)
        {
            if (target.TryGetValue(name, out var definition))
            {
                processedTargets.Add(name);
                if (!BlueTuskEventTriggerMetadata.CreateBodyEquals(oldDefinition, definition))
                {
                    before.Add(new DropEventTriggerOperation
                    {
                        Name = name,
                        IsDestructiveChange = true,
                    });
                    after.Add(new CreateEventTriggerOperation { Definition = definition });
                }
                else if (oldDefinition.EnabledMode != definition.EnabledMode)
                {
                    after.Add(new AlterEventTriggerEnabledModeOperation
                    {
                        Name = name,
                        EnabledMode = definition.EnabledMode,
                    });
                }

                continue;
            }

            var renameCandidates = target.Values.Where(candidate =>
                    !processedTargets.Contains(candidate.Name) &&
                    !source.ContainsKey(candidate.Name) &&
                    BlueTuskEventTriggerMetadata.CreateBodyEquals(oldDefinition, candidate) &&
                    oldDefinition.EnabledMode == candidate.EnabledMode)
                .ToArray();
            if (renameCandidates.Length == 1)
            {
                var renamed = renameCandidates[0];
                processedTargets.Add(renamed.Name);
                after.Add(new RenameEventTriggerOperation { Name = name, NewName = renamed.Name });
            }
            else
            {
                before.Add(new DropEventTriggerOperation
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
                after.Add(new CreateEventTriggerOperation { Definition = definition });
            }
        }
    }

    private static BlueTuskEventTriggerDefinitionSet Get(IRelationalModel? model) => model is null
        ? BlueTuskEventTriggerDefinitionSet.Empty
        : BlueTuskEventTriggerMetadata.Get(model.Model);
}

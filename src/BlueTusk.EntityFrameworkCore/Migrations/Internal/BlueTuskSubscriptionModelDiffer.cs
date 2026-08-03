using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Subscriptions;
using BlueTusk.EntityFrameworkCore.Subscriptions.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskSubscriptionModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !DefinitionSetsEqual(Get(source), Get(target));

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var source = Get(sourceModel).Subscriptions.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var target = Get(targetModel).Subscriptions.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var unmatched = new Dictionary<string, BlueTuskSubscriptionDefinition>(target, StringComparer.Ordinal);
        foreach (var oldDefinition in source.Values)
        {
            if (target.TryGetValue(oldDefinition.Name, out var definition))
            {
                unmatched.Remove(definition.Name);
                if (!DefinitionEquals(oldDefinition, definition))
                {
                    after.Add(new AlterBlueTuskSubscriptionOperation
                    {
                        OldDefinition = oldDefinition,
                        Definition = definition,
                    });
                }

                continue;
            }

            var candidates = unmatched.Values.Where(candidate => BodyEquals(oldDefinition, candidate)).ToArray();
            if (candidates.Length == 1)
            {
                var renamed = candidates[0];
                after.Add(new RenameBlueTuskSubscriptionOperation
                {
                    Name = oldDefinition.Name,
                    NewName = renamed.Name,
                });
                unmatched.Remove(renamed.Name);
            }
            else
            {
                before.Add(new DropBlueTuskSubscriptionOperation
                {
                    Name = oldDefinition.Name,
                    HasSlot = oldDefinition.SlotName is not null,
                    IsDestructiveChange = true,
                });
            }
        }

        foreach (var definition in unmatched.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            after.Add(new CreateBlueTuskSubscriptionOperation { Definition = definition });
        }
    }

    private static bool DefinitionEquals(
        BlueTuskSubscriptionDefinition left,
        BlueTuskSubscriptionDefinition right,
        bool includeName = true)
    {
        left = BlueTuskSubscriptionMetadata.Normalize(left);
        right = BlueTuskSubscriptionMetadata.Normalize(right);
        return (!includeName || string.Equals(left.Name, right.Name, StringComparison.Ordinal)) &&
               left.Connection == right.Connection &&
               left.Publications.SequenceEqual(right.Publications, StringComparer.Ordinal) &&
               string.Equals(left.SlotName, right.SlotName, StringComparison.Ordinal) &&
               left.Enabled == right.Enabled &&
               left.Binary == right.Binary &&
               left.Streaming == right.Streaming &&
               left.SynchronousCommit == right.SynchronousCommit &&
               left.TwoPhase == right.TwoPhase &&
               left.DisableOnError == right.DisableOnError &&
               left.PasswordRequired == right.PasswordRequired &&
               left.RunAsOwner == right.RunAsOwner &&
               left.Origin == right.Origin &&
               left.Failover == right.Failover &&
               left.RetainDeadTuples == right.RetainDeadTuples &&
               left.MaxRetentionDuration == right.MaxRetentionDuration &&
               string.Equals(left.WalReceiverTimeout, right.WalReceiverTimeout, StringComparison.Ordinal);
    }

    private static bool BodyEquals(BlueTuskSubscriptionDefinition left, BlueTuskSubscriptionDefinition right) =>
        DefinitionEquals(left, right, includeName: false);

    private static bool DefinitionSetsEqual(
        BlueTuskSubscriptionDefinitionSet left,
        BlueTuskSubscriptionDefinitionSet right)
    {
        if (left.Subscriptions.Count != right.Subscriptions.Count)
        {
            return false;
        }

        var targets = right.Subscriptions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);
        return left.Subscriptions.All(definition =>
            targets.TryGetValue(definition.Name, out var target) && DefinitionEquals(definition, target));
    }

    private static BlueTuskSubscriptionDefinitionSet Get(IRelationalModel? model) =>
        model is null ? BlueTuskSubscriptionDefinitionSet.Empty : BlueTuskSubscriptionMetadata.Get(model.Model);
}

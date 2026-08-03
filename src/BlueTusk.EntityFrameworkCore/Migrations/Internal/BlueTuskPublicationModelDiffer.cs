using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Publications;
using BlueTusk.EntityFrameworkCore.Publications.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskPublicationModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskPublicationMetadata.Serialize(Get(source)),
            BlueTuskPublicationMetadata.Serialize(Get(target)),
            StringComparison.Ordinal);

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var source = Get(sourceModel).Publications.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var target = Get(targetModel).Publications.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var unmatched = new Dictionary<string, BlueTuskPublicationDefinition>(target, StringComparer.Ordinal);
        foreach (var oldDefinition in source.Values)
        {
            if (target.TryGetValue(oldDefinition.Name, out var definition))
            {
                unmatched.Remove(definition.Name);
                if (DefinitionEquals(oldDefinition, definition))
                {
                    continue;
                }

                if (oldDefinition.AllTables != definition.AllTables ||
                    oldDefinition.AllSequences != definition.AllSequences)
                {
                    before.Add(Drop(oldDefinition.Name));
                    after.Add(new CreateBlueTuskPublicationOperation { Definition = definition });
                }
                else
                {
                    var operation = new AlterBlueTuskPublicationOperation
                    {
                        OldDefinition = oldDefinition,
                        Definition = definition,
                    };
                    if (HasMembership(definition))
                    {
                        after.Add(operation);
                    }
                    else
                    {
                        before.Add(operation);
                    }
                }

                continue;
            }

            var candidates = unmatched.Values.Where(candidate => BodyEquals(oldDefinition, candidate)).ToArray();
            if (candidates.Length == 1)
            {
                var renamed = candidates[0];
                after.Add(new RenameBlueTuskPublicationOperation
                {
                    Name = oldDefinition.Name,
                    NewName = renamed.Name,
                });
                unmatched.Remove(renamed.Name);
            }
            else
            {
                before.Add(Drop(oldDefinition.Name));
            }
        }

        foreach (var definition in unmatched.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            after.Add(new CreateBlueTuskPublicationOperation { Definition = definition });
        }
    }

    private static bool HasMembership(BlueTuskPublicationDefinition definition) =>
        definition.AllTables || definition.AllSequences || definition.Tables.Count > 0 || definition.Schemas.Count > 0;

    private static DropBlueTuskPublicationOperation Drop(string name) =>
        new() { Name = name, IsDestructiveChange = true };

    private static bool DefinitionEquals(
        BlueTuskPublicationDefinition left,
        BlueTuskPublicationDefinition right) =>
        string.Equals(
            BlueTuskPublicationMetadata.Serialize(left),
            BlueTuskPublicationMetadata.Serialize(right),
            StringComparison.Ordinal);

    private static bool BodyEquals(BlueTuskPublicationDefinition left, BlueTuskPublicationDefinition right) =>
        DefinitionEquals(left with { Name = "_" }, right with { Name = "_" });

    private static BlueTuskPublicationDefinitionSet Get(IRelationalModel? model) =>
        model is null ? BlueTuskPublicationDefinitionSet.Empty : BlueTuskPublicationMetadata.Get(model.Model);
}

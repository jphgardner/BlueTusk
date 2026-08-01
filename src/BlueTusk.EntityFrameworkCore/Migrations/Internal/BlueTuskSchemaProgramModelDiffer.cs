using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.SchemaPrograms;
using BlueTusk.EntityFrameworkCore.SchemaPrograms.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskSchemaProgramModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        BlueTuskSchemaProgramMetadata.Serialize(Get(source)) != BlueTuskSchemaProgramMetadata.Serialize(Get(target));

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var source = Get(sourceModel);
        var target = Get(targetModel);
        var operatorAfter = new List<MigrationOperation>();
        var familyAfter = new List<MigrationOperation>();
        var classAfter = new List<MigrationOperation>();
        var castAfter = new List<MigrationOperation>();
        var aggregateAfter = new List<MigrationOperation>();
        DiffOperators(source, target, before, operatorAfter);
        DiffFamilies(source, target, before, familyAfter);
        DiffClasses(source, target, before, classAfter);
        DiffCasts(source, target, before, castAfter);
        DiffAggregates(source, target, before, aggregateAfter);
        foreach (var operation in aggregateAfter.Concat(castAfter).Concat(classAfter).Concat(familyAfter)
                     .Concat(operatorAfter))
        {
            after.Add(operation);
        }
    }

    private static void DiffOperators(
        BlueTuskSchemaProgramDefinitionSet source,
        BlueTuskSchemaProgramDefinitionSet target,
        ICollection<MigrationOperation> before,
        List<MigrationOperation> after)
    {
        var oldItems = source.Operators.ToDictionary(BlueTuskSchemaProgramMetadata.OperatorKey.Create);
        var newItems = target.Operators.ToDictionary(BlueTuskSchemaProgramMetadata.OperatorKey.Create);
        foreach (var (key, oldDefinition) in oldItems)
        {
            if (newItems.TryGetValue(key, out var definition))
            {
                if (!Equal(oldDefinition, definition))
                {
                    before.Add(new ReplaceBlueTuskOperatorOperation
                    {
                        OldDefinition = oldDefinition,
                        Definition = definition,
                        IsDestructiveChange = true,
                    });
                }
            }
            else
            {
                after.Add(new DropBlueTuskOperatorOperation
                {
                    Definition = oldDefinition,
                    IsDestructiveChange = true,
                });
            }
        }

        foreach (var (key, definition) in newItems)
        {
            if (!oldItems.ContainsKey(key)) before.Add(new CreateBlueTuskOperatorOperation { Definition = definition });
        }
    }

    private static void DiffFamilies(
        BlueTuskSchemaProgramDefinitionSet source,
        BlueTuskSchemaProgramDefinitionSet target,
        ICollection<MigrationOperation> before,
        List<MigrationOperation> after)
    {
        var oldItems = source.OperatorFamilies.ToDictionary(BlueTuskSchemaProgramMetadata.OperatorFamilyKey.Create);
        var newItems = target.OperatorFamilies.ToDictionary(BlueTuskSchemaProgramMetadata.OperatorFamilyKey.Create);
        foreach (var (key, oldDefinition) in oldItems)
        {
            if (newItems.TryGetValue(key, out var definition))
            {
                if (!Equal(oldDefinition, definition))
                {
                    before.Add(new AlterBlueTuskOperatorFamilyOperation
                    {
                        OldDefinition = oldDefinition,
                        Definition = definition,
                        IsDestructiveChange = true,
                    });
                }
            }
            else
            {
                after.Add(new DropBlueTuskOperatorFamilyOperation
                {
                    Definition = oldDefinition,
                    IsDestructiveChange = true,
                });
            }
        }

        foreach (var (key, definition) in newItems)
        {
            if (!oldItems.ContainsKey(key))
            {
                before.Add(new CreateBlueTuskOperatorFamilyOperation { Definition = definition });
            }
        }
    }

    private static void DiffClasses(
        BlueTuskSchemaProgramDefinitionSet source,
        BlueTuskSchemaProgramDefinitionSet target,
        ICollection<MigrationOperation> before,
        List<MigrationOperation> after)
    {
        var oldItems = source.OperatorClasses.ToDictionary(BlueTuskSchemaProgramMetadata.OperatorClassKey.Create);
        var newItems = target.OperatorClasses.ToDictionary(BlueTuskSchemaProgramMetadata.OperatorClassKey.Create);
        foreach (var (key, oldDefinition) in oldItems)
        {
            if (newItems.TryGetValue(key, out var definition))
            {
                if (!Equal(oldDefinition, definition))
                {
                    before.Add(new ReplaceBlueTuskOperatorClassOperation
                    {
                        OldDefinition = oldDefinition,
                        Definition = definition,
                        IsDestructiveChange = true,
                    });
                }
            }
            else
            {
                after.Add(new DropBlueTuskOperatorClassOperation
                {
                    Definition = oldDefinition,
                    IsDestructiveChange = true,
                });
            }
        }

        foreach (var (key, definition) in newItems)
        {
            if (!oldItems.ContainsKey(key))
            {
                before.Add(new CreateBlueTuskOperatorClassOperation { Definition = definition });
            }
        }
    }

    private static void DiffCasts(
        BlueTuskSchemaProgramDefinitionSet source,
        BlueTuskSchemaProgramDefinitionSet target,
        ICollection<MigrationOperation> before,
        List<MigrationOperation> after)
    {
        var oldItems = source.Casts.ToDictionary(BlueTuskSchemaProgramMetadata.CastKey.Create);
        var newItems = target.Casts.ToDictionary(BlueTuskSchemaProgramMetadata.CastKey.Create);
        foreach (var (key, oldDefinition) in oldItems)
        {
            if (newItems.TryGetValue(key, out var definition))
            {
                if (!Equal(oldDefinition, definition))
                {
                    before.Add(new ReplaceBlueTuskCastOperation
                    {
                        OldDefinition = oldDefinition,
                        Definition = definition,
                        IsDestructiveChange = true,
                    });
                }
            }
            else
            {
                after.Add(new DropBlueTuskCastOperation
                {
                    Definition = oldDefinition,
                    IsDestructiveChange = true,
                });
            }
        }

        foreach (var (key, definition) in newItems)
        {
            if (!oldItems.ContainsKey(key)) before.Add(new CreateBlueTuskCastOperation { Definition = definition });
        }
    }

    private static void DiffAggregates(
        BlueTuskSchemaProgramDefinitionSet source,
        BlueTuskSchemaProgramDefinitionSet target,
        ICollection<MigrationOperation> before,
        List<MigrationOperation> after)
    {
        var oldItems = source.Aggregates.ToDictionary(BlueTuskSchemaProgramMetadata.AggregateKey.Create);
        var newItems = target.Aggregates.ToDictionary(BlueTuskSchemaProgramMetadata.AggregateKey.Create);
        foreach (var (key, oldDefinition) in oldItems)
        {
            if (newItems.TryGetValue(key, out var definition))
            {
                if (!Equal(oldDefinition, definition))
                {
                    before.Add(new ReplaceBlueTuskAggregateOperation
                    {
                        OldDefinition = oldDefinition,
                        Definition = definition,
                        IsDestructiveChange = true,
                    });
                }
            }
            else
            {
                after.Add(new DropBlueTuskAggregateOperation
                {
                    Definition = oldDefinition,
                    IsDestructiveChange = true,
                });
            }
        }

        foreach (var (key, definition) in newItems)
        {
            if (!oldItems.ContainsKey(key))
            {
                before.Add(new CreateBlueTuskAggregateOperation { Definition = definition });
            }
        }
    }

    private static bool Equal<T>(T left, T right) =>
        BlueTuskSchemaProgramMetadata.Serialize(left) == BlueTuskSchemaProgramMetadata.Serialize(right);

    private static BlueTuskSchemaProgramDefinitionSet Get(IRelationalModel? model) =>
        model is null ? BlueTuskSchemaProgramDefinitionSet.Empty : BlueTuskSchemaProgramMetadata.Get(model.Model);

}

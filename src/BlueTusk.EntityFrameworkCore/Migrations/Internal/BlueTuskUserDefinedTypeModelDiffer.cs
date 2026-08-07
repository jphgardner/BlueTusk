using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes.Internal;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskUserDefinedTypeModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskUserDefinedTypeMetadata.Serialize(GetDefinitions(source)),
            BlueTuskUserDefinedTypeMetadata.Serialize(GetDefinitions(target)),
            StringComparison.Ordinal);

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        IReadOnlyList<MigrationOperation> baseOperations,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var source = GetItems(GetDefinitions(sourceModel)).ToDictionary(item => item.Key);
        var target = GetItems(GetDefinitions(targetModel)).ToDictionary(item => item.Key);
        foreach (var key in source.Keys.Intersect(target.Keys))
        {
            if (source[key].Kind != target[key].Kind)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL type '{key.Schema}.{key.Name}' cannot change from {source[key].Kind} to {target[key].Kind} in place. " +
                    "Create an explicit data-preserving replacement migration.");
            }
        }

        var unmatchedTargets = new Dictionary<TypeKey, TypeItem>(target);
        var creates = new List<TypeItem>();
        var drops = new List<TypeItem>();
        var targetOperations = new List<TargetOperation>();
        foreach (var (key, sourceItem) in source)
        {
            if (target.TryGetValue(key, out var targetItem))
            {
                unmatchedTargets.Remove(key);
                if (!BodyEquals(sourceItem, targetItem))
                {
                    targetOperations.Add(new TargetOperation(
                        targetItem,
                        CreateAlter(sourceItem, targetItem)));
                }
                else if (sourceItem.Definition is BlueTuskRangeTypeDefinition sourceRange &&
                         targetItem.Definition is BlueTuskRangeTypeDefinition targetRange &&
                         !Equals(sourceRange.MultirangeType, targetRange.MultirangeType))
                {
                    targetOperations.Add(new TargetOperation(
                        targetItem,
                        CreateRangeRename(sourceRange, targetRange)));
                }

                continue;
            }

            var renameCandidates = unmatchedTargets.Values
                .Where(candidate => candidate.Kind == sourceItem.Kind && BodyEquals(sourceItem, candidate))
                .ToArray();
            if (renameCandidates.Length == 1)
            {
                var renamed = renameCandidates[0];
                targetOperations.Add(new TargetOperation(
                    renamed,
                    sourceItem.Definition is BlueTuskRangeTypeDefinition sourceRange &&
                    renamed.Definition is BlueTuskRangeTypeDefinition targetRange
                        ? CreateRangeRename(sourceRange, targetRange)
                        : new RenameUserDefinedTypeOperation
                        {
                            Kind = sourceItem.Kind,
                            Name = sourceItem.Key.Name,
                            Schema = sourceItem.Key.Schema,
                            NewName = renamed.Key.Name,
                            NewSchema = renamed.Key.Schema,
                        }));
                unmatchedTargets.Remove(renamed.Key);
            }
            else
            {
                drops.Add(sourceItem);
            }
        }

        creates.AddRange(unmatchedTargets.Values);
        targetOperations.AddRange(creates.Select(item => new TargetOperation(item, CreateCreate(item))));
        var schemasToEnsure = creates.SelectMany(item => item.Definition is BlueTuskRangeTypeDefinition range
                ? new[] { item.Key.Schema, range.MultirangeType.Schema }
                : [item.Key.Schema])
            .Concat(targetOperations.Select(item => item.Operation)
                .OfType<RenameUserDefinedTypeOperation>()
                .Where(operation =>
                    operation.NewSchema is not null &&
                    !string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
                .Select(operation => operation.NewSchema))
            .Concat(targetOperations.Select(item => item.Operation)
                .OfType<RenameRangeTypeOperation>()
                .SelectMany(operation => new[]
                {
                    !string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal)
                        ? operation.NewSchema
                        : null,
                    !string.Equals(operation.MultirangeSchema, operation.NewMultirangeSchema, StringComparison.Ordinal)
                        ? operation.NewMultirangeSchema
                        : null,
                }))
            .Where(schema => schema is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(schema => !baseOperations.OfType<EnsureSchemaOperation>()
                .Any(operation => string.Equals(operation.Name, schema, StringComparison.Ordinal)));
        foreach (var schema in schemasToEnsure)
        {
            before.Add(new EnsureSchemaOperation { Name = schema });
        }

        foreach (var operation in OrderForApplication(targetOperations))
        {
            before.Add(operation);
        }

        foreach (var item in OrderForCreation(drops).AsEnumerable().Reverse())
        {
            after.Add(CreateDrop(item));
        }
    }

    private static MigrationOperation CreateAlter(TypeItem source, TypeItem target) =>
        target.Kind switch
        {
            BlueTuskUserDefinedTypeKind.Enum => CreateEnumAlter(source, target),
            BlueTuskUserDefinedTypeKind.Domain => CreateDomainAlter(source, target),
            BlueTuskUserDefinedTypeKind.Composite => CreateCompositeAlter(source, target),
            BlueTuskUserDefinedTypeKind.Range => throw new InvalidOperationException(
                $"PostgreSQL range type '{target.Key.Schema}.{target.Key.Name}' cannot change its subtype, operator class, " +
                "collation, canonical function, or subtype-difference function in place. Create an explicit " +
                "data-preserving replacement migration."),
            _ => throw new InvalidOperationException($"Unknown PostgreSQL type kind '{target.Kind}'."),
        };

    private static AlterEnumTypeOperation CreateEnumAlter(TypeItem source, TypeItem target)
    {
        var oldDefinition = (BlueTuskEnumTypeDefinition)source.Definition;
        var definition = (BlueTuskEnumTypeDefinition)target.Definition;
        _ = BlueTuskUserDefinedTypeAlterationPlanner.PlanEnum(oldDefinition, definition);
        return new AlterEnumTypeOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
        };
    }

    private static AlterDomainTypeOperation CreateDomainAlter(TypeItem source, TypeItem target)
    {
        var oldDefinition = (BlueTuskDomainTypeDefinition)source.Definition;
        var definition = (BlueTuskDomainTypeDefinition)target.Definition;
        BlueTuskUserDefinedTypeAlterationPlanner.ValidateDomain(oldDefinition, definition);
        return new AlterDomainTypeOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
        };
    }

    private static AlterCompositeTypeOperation CreateCompositeAlter(TypeItem source, TypeItem target)
    {
        var oldDefinition = (BlueTuskCompositeTypeDefinition)source.Definition;
        var definition = (BlueTuskCompositeTypeDefinition)target.Definition;
        var changes = BlueTuskUserDefinedTypeAlterationPlanner.PlanComposite(oldDefinition, definition);
        return new AlterCompositeTypeOperation
        {
            OldDefinition = oldDefinition,
            Definition = definition,
            IsDestructiveChange = changes.Any(change => change.Kind == CompositeAttributeChangeKind.Drop),
        };
    }

    private static MigrationOperation CreateCreate(TypeItem item) =>
        item.Kind switch
        {
            BlueTuskUserDefinedTypeKind.Enum =>
                new CreateEnumTypeOperation { Definition = (BlueTuskEnumTypeDefinition)item.Definition },
            BlueTuskUserDefinedTypeKind.Domain =>
                new CreateDomainTypeOperation { Definition = (BlueTuskDomainTypeDefinition)item.Definition },
            BlueTuskUserDefinedTypeKind.Composite =>
                new CreateCompositeTypeOperation { Definition = (BlueTuskCompositeTypeDefinition)item.Definition },
            BlueTuskUserDefinedTypeKind.Range =>
                new CreateRangeTypeOperation { Definition = (BlueTuskRangeTypeDefinition)item.Definition },
            _ => throw new InvalidOperationException($"Unknown PostgreSQL type kind '{item.Kind}'."),
        };

    private static MigrationOperation CreateDrop(TypeItem item) =>
        item.Kind switch
        {
            BlueTuskUserDefinedTypeKind.Enum => new DropEnumTypeOperation
            {
                Name = item.Key.Name,
                Schema = item.Key.Schema,
                IsDestructiveChange = true,
            },
            BlueTuskUserDefinedTypeKind.Domain => new DropDomainTypeOperation
            {
                Name = item.Key.Name,
                Schema = item.Key.Schema,
                IsDestructiveChange = true,
            },
            BlueTuskUserDefinedTypeKind.Composite => new DropCompositeTypeOperation
            {
                Name = item.Key.Name,
                Schema = item.Key.Schema,
                IsDestructiveChange = true,
            },
            BlueTuskUserDefinedTypeKind.Range => new DropRangeTypeOperation
            {
                Name = item.Key.Name,
                Schema = item.Key.Schema,
                IsDestructiveChange = true,
            },
            _ => throw new InvalidOperationException($"Unknown PostgreSQL type kind '{item.Kind}'."),
        };

    private static List<TypeItem> OrderForCreation(IReadOnlyCollection<TypeItem> items)
    {
        var byKey = items.ToDictionary(item => item.Key);
        var ownerByTypeKey = new Dictionary<TypeKey, TypeKey>();
        foreach (var item in items)
        {
            ownerByTypeKey[item.Key] = item.Key;
            if (item.Definition is BlueTuskRangeTypeDefinition range)
            {
                ownerByTypeKey[new TypeKey(range.MultirangeType.Schema, range.MultirangeType.Name)] = item.Key;
            }
        }

        var remaining = items.ToDictionary(
            item => item.Key,
            item => GetDependencies(item)
                .Select(dependency => ownerByTypeKey.TryGetValue(dependency, out var owner) ? owner : dependency)
                .Where(dependency => dependency != item.Key && byKey.ContainsKey(dependency))
                .ToHashSet());
        var ordered = new List<TypeItem>(items.Count);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(pair => pair.Value.Count == 0)
                .Select(pair => byKey[pair.Key])
                .OrderBy(item => item.Kind)
                .ThenBy(item => item.Key.Schema, StringComparer.Ordinal)
                .ThenBy(item => item.Key.Name, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
            {
                throw new InvalidOperationException(
                    "PostgreSQL user-defined type definitions contain a cyclic creation dependency.");
            }

            foreach (var item in ready)
            {
                ordered.Add(item);
                remaining.Remove(item.Key);
                foreach (var dependencies in remaining.Values)
                {
                    dependencies.Remove(item.Key);
                }
            }
        }

        return ordered;
    }

    private static IEnumerable<MigrationOperation> OrderForApplication(
        IReadOnlyCollection<TargetOperation> operations)
    {
        var byKey = operations.ToDictionary(operation => operation.Item.Key);
        foreach (var item in OrderForCreation(operations.Select(operation => operation.Item).ToArray()))
        {
            yield return byKey[item.Key].Operation;
        }
    }

    private static IEnumerable<TypeKey> GetDependencies(TypeItem item)
    {
        if (item.Definition is BlueTuskRangeTypeDefinition range)
        {
            yield return new TypeKey(range.Subtype.Schema, range.Subtype.Name);
            yield break;
        }

        var storeTypes = item.Definition switch
        {
            BlueTuskDomainTypeDefinition domain => [domain.BaseStoreType],
            BlueTuskCompositeTypeDefinition composite => composite.Attributes
                .Select(attribute => attribute.StoreType),
            _ => Array.Empty<string>(),
        };
        foreach (var storeType in storeTypes)
        {
            var typeName = storeType.Trim();
            while (typeName.EndsWith("[]", StringComparison.Ordinal))
            {
                typeName = typeName[..^2].TrimEnd();
            }

            TypeKey? dependency = null;
            try
            {
                var parsed = BlueTuskTypeName.Parse(typeName);
                dependency = new TypeKey(parsed.Schema, parsed.Name);
            }
            catch (FormatException)
            {
                // Built-in and typmod-bearing store types do not identify provider-owned dependencies.
            }

            if (dependency is { } resolved)
            {
                yield return resolved;
            }
        }
    }

    private static IEnumerable<TypeItem> GetItems(BlueTuskUserDefinedTypeDefinitionSet definitions) =>
        definitions.Enums.Select(definition => new TypeItem(
                BlueTuskUserDefinedTypeKind.Enum,
                new TypeKey(definition.Schema, definition.Name),
                definition))
            .Concat(definitions.Domains.Select(definition => new TypeItem(
                BlueTuskUserDefinedTypeKind.Domain,
                new TypeKey(definition.Schema, definition.Name),
                definition)))
            .Concat(definitions.Composites.Select(definition => new TypeItem(
                BlueTuskUserDefinedTypeKind.Composite,
                new TypeKey(definition.Schema, definition.Name),
                definition)))
            .Concat(definitions.Ranges.Select(definition => new TypeItem(
                BlueTuskUserDefinedTypeKind.Range,
                new TypeKey(definition.Schema, definition.Name),
                definition)));

    private static BlueTuskUserDefinedTypeDefinitionSet GetDefinitions(IRelationalModel? model) =>
        model is null
            ? BlueTuskUserDefinedTypeDefinitionSet.Empty
            : BlueTuskUserDefinedTypeMetadata.Get(model.Model);

    private static bool BodyEquals(TypeItem left, TypeItem right) =>
        left.Kind == right.Kind &&
        string.Equals(SerializeBody(left), SerializeBody(right), StringComparison.Ordinal);

    private static string SerializeBody(TypeItem item) =>
        item.Definition switch
        {
            BlueTuskEnumTypeDefinition definition =>
                BlueTuskUserDefinedTypeMetadata.Serialize(definition with { Name = "_", Schema = null }),
            BlueTuskDomainTypeDefinition definition =>
                BlueTuskUserDefinedTypeMetadata.Serialize(definition with { Name = "_", Schema = null }),
            BlueTuskCompositeTypeDefinition definition =>
                BlueTuskUserDefinedTypeMetadata.Serialize(definition with { Name = "_", Schema = null }),
            BlueTuskRangeTypeDefinition definition =>
                BlueTuskUserDefinedTypeMetadata.Serialize(definition with
                {
                    Name = "_",
                    Schema = null,
                    MultirangeType = new BlueTuskQualifiedName("__"),
                }),
            _ => throw new InvalidOperationException($"Unknown PostgreSQL type definition '{item.Definition.GetType().Name}'."),
        };

    private static RenameRangeTypeOperation CreateRangeRename(
        BlueTuskRangeTypeDefinition source,
        BlueTuskRangeTypeDefinition target) =>
        new()
        {
            Name = source.Name,
            Schema = source.Schema,
            NewName = target.Name,
            NewSchema = target.Schema,
            MultirangeName = source.MultirangeType.Name,
            MultirangeSchema = source.MultirangeType.Schema,
            NewMultirangeName = target.MultirangeType.Name,
            NewMultirangeSchema = target.MultirangeType.Schema,
        };

    private readonly record struct TypeKey(string? Schema, string Name);

    private sealed record TypeItem(
        BlueTuskUserDefinedTypeKind Kind,
        TypeKey Key,
        object Definition);

    private sealed record TargetOperation(TypeItem Item, MigrationOperation Operation);
}

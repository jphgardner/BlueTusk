using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Partitioning;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

#pragma warning disable EF1001 // Provider implementation requires EF Core infrastructure services.

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal sealed class BlueTuskMigrationsModelDiffer(
    IRelationalTypeMappingSource typeMappingSource,
    IMigrationsAnnotationProvider migrationsAnnotationProvider,
    IRelationalAnnotationProvider relationalAnnotationProvider,
    IRowIdentityMapFactory rowIdentityMapFactory,
    CommandBatchPreparerDependencies commandBatchPreparerDependencies)
    : Microsoft.EntityFrameworkCore.Migrations.Internal.MigrationsModelDiffer(
        typeMappingSource,
        migrationsAnnotationProvider,
        relationalAnnotationProvider,
        rowIdentityMapFactory,
        commandBatchPreparerDependencies)
{
    public override bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        base.HasDifferences(source, target) ||
        BlueTuskCheckConstraintModelDiffer.HasDifferences(source, target) ||
        !DefinitionsEqual(GetGraphs(source), GetGraphs(target)) ||
        !PartitionDefinitionsEqual(GetPartitions(source), GetPartitions(target)) ||
        BlueTuskRowLevelSecurityModelDiffer.HasDifferences(source, target) ||
        BlueTuskTableInheritanceModelDiffer.HasDifferences(source, target) ||
        BlueTuskUserDefinedTypeModelDiffer.HasDifferences(source, target) ||
        BlueTuskRoutineModelDiffer.HasDifferences(source, target) ||
        BlueTuskViewModelDiffer.HasDifferences(source, target) ||
        BlueTuskExtensionModelDiffer.HasDifferences(source, target) ||
        BlueTuskCollationModelDiffer.HasDifferences(source, target) ||
        BlueTuskExclusionConstraintModelDiffer.HasDifferences(source, target) ||
        BlueTuskTriggerModelDiffer.HasDifferences(source, target) ||
        BlueTuskRuleModelDiffer.HasDifferences(source, target) ||
        BlueTuskPublicationModelDiffer.HasDifferences(source, target) ||
        BlueTuskSubscriptionModelDiffer.HasDifferences(source, target) ||
        BlueTuskForeignDataModelDiffer.HasDifferences(source, target) ||
        BlueTuskSchemaProgramModelDiffer.HasDifferences(source, target) ||
        BlueTuskEventTriggerModelDiffer.HasDifferences(source, target) ||
        BlueTuskTablespaceModelDiffer.HasDifferences(source, target);

    public override IReadOnlyList<MigrationOperation> GetDifferences(
        IRelationalModel? source,
        IRelationalModel? target)
    {
        var baseOperations = base.GetDifferences(source, target);
        var sourceGraphs = GetGraphs(source);
        var targetGraphs = GetGraphs(target);
        var sourceByName = sourceGraphs.ToDictionary(GraphKey.Create);
        var targetByName = targetGraphs.ToDictionary(GraphKey.Create);
        var typeBefore = new List<MigrationOperation>();
        var typeAfter = new List<MigrationOperation>();
        var extensionBefore = new List<MigrationOperation>();
        var extensionAfter = new List<MigrationOperation>();
        var collationBefore = new List<MigrationOperation>();
        var collationAfter = new List<MigrationOperation>();
        var routineBefore = new List<MigrationOperation>();
        var routineAfter = new List<MigrationOperation>();
        var viewBefore = new List<MigrationOperation>();
        var viewAfter = new List<MigrationOperation>();
        var triggerBefore = new List<MigrationOperation>();
        var triggerAfter = new List<MigrationOperation>();
        var ruleBefore = new List<MigrationOperation>();
        var ruleAfter = new List<MigrationOperation>();
        var publicationBefore = new List<MigrationOperation>();
        var publicationAfter = new List<MigrationOperation>();
        var subscriptionBefore = new List<MigrationOperation>();
        var subscriptionAfter = new List<MigrationOperation>();
        var foreignDataBefore = new List<MigrationOperation>();
        var foreignDataAfter = new List<MigrationOperation>();
        var schemaProgramBefore = new List<MigrationOperation>();
        var schemaProgramAfter = new List<MigrationOperation>();
        var eventTriggerBefore = new List<MigrationOperation>();
        var eventTriggerAfter = new List<MigrationOperation>();
        var tablespaceBefore = new List<MigrationOperation>();
        var tablespaceAfter = new List<MigrationOperation>();
        var before = new List<MigrationOperation>();
        var after = new List<MigrationOperation>();

        BlueTuskExtensionModelDiffer.AddDifferences(
            source,
            target,
            baseOperations,
            extensionBefore,
            extensionAfter);
        BlueTuskCollationModelDiffer.AddDifferences(
            source,
            target,
            baseOperations,
            collationBefore,
            collationAfter);
        BlueTuskUserDefinedTypeModelDiffer.AddDifferences(
            source,
            target,
            baseOperations,
            typeBefore,
            typeAfter);
        BlueTuskRoutineModelDiffer.AddDifferences(
            source,
            target,
            baseOperations,
            routineBefore,
            routineAfter);
        BlueTuskViewModelDiffer.AddDifferences(
            source,
            target,
            baseOperations,
            viewBefore,
            viewAfter);
        BlueTuskTriggerModelDiffer.AddDifferences(
            source,
            target,
            baseOperations,
            triggerBefore,
            triggerAfter);
        BlueTuskRuleModelDiffer.AddDifferences(
            source,
            target,
            baseOperations,
            ruleBefore,
            ruleAfter);
        BlueTuskPublicationModelDiffer.AddDifferences(
            source,
            target,
            publicationBefore,
            publicationAfter);
        BlueTuskSubscriptionModelDiffer.AddDifferences(
            source,
            target,
            subscriptionBefore,
            subscriptionAfter);
        BlueTuskForeignDataModelDiffer.AddDifferences(
            source,
            target,
            baseOperations,
            foreignDataBefore,
            foreignDataAfter);
        BlueTuskSchemaProgramModelDiffer.AddDifferences(
            source,
            target,
            schemaProgramBefore,
            schemaProgramAfter);
        BlueTuskEventTriggerModelDiffer.AddDifferences(
            source,
            target,
            eventTriggerBefore,
            eventTriggerAfter);
        BlueTuskTablespaceModelDiffer.AddDifferences(
            source,
            target,
            tablespaceBefore,
            tablespaceAfter);
        BlueTuskExclusionConstraintModelDiffer.AddDifferences(
            source,
            target,
            baseOperations,
            before,
            after);
        BlueTuskCheckConstraintModelDiffer.AddDifferences(source, target, baseOperations, before, after);
        AddPartitionDifferences(source, target, baseOperations, before, after);
        BlueTuskRowLevelSecurityModelDiffer.AddDifferences(source, target, baseOperations, after);
        BlueTuskTableInheritanceModelDiffer.AddDifferences(
            source,
            target,
            baseOperations,
            before,
            after);

        foreach (var (key, sourceGraph) in sourceByName)
        {
            if (targetByName.TryGetValue(key, out var targetGraph))
            {
                if (!DefinitionEquals(sourceGraph, targetGraph))
                {
                    before.Add(CreateDrop(sourceGraph));
                    after.Add(CreateCreate(targetGraph));
                }

                continue;
            }

            var renameCandidates = targetGraphs
                .Where(targetGraph =>
                    targetByName.ContainsKey(GraphKey.Create(targetGraph)) &&
                    !sourceByName.ContainsKey(GraphKey.Create(targetGraph)) &&
                    DefinitionBodyEquals(sourceGraph, targetGraph))
                .ToArray();
            if (renameCandidates.Length == 1)
            {
                var renamed = renameCandidates[0];
                before.Add(new AlterBlueTuskPropertyGraphOperation
                {
                    Name = sourceGraph.Name,
                    Schema = sourceGraph.Schema,
                    NewName = renamed.Name,
                    NewSchema = renamed.Schema,
                });
                targetByName.Remove(GraphKey.Create(renamed));
            }
            else
            {
                before.Add(CreateDrop(sourceGraph));
            }
        }

        foreach (var (key, targetGraph) in targetByName)
        {
            if (!sourceByName.ContainsKey(key))
            {
                after.Add(CreateCreate(targetGraph));
            }
        }

        var ensureSchemas = baseOperations.OfType<EnsureSchemaOperation>();
        var dropSchemas = baseOperations.OfType<DropSchemaOperation>();
        var relationalOperations = baseOperations.Where(
            operation => operation is not EnsureSchemaOperation and not DropSchemaOperation);
        var operations = eventTriggerBefore.Concat(tablespaceBefore).Concat(ensureSchemas).Concat(extensionBefore)
            .Concat(collationBefore)
            .Concat(typeBefore)
            .Concat(subscriptionBefore)
            .Concat(publicationBefore)
            .Concat(ruleBefore)
            .Concat(triggerBefore)
            .Concat(viewBefore)
            .Concat(routineBefore)
            .Concat(schemaProgramBefore)
            .Concat(foreignDataBefore)
            .Concat(before)
            .Concat(relationalOperations)
            .Concat(after)
            .Concat(foreignDataAfter)
            .Concat(schemaProgramAfter)
            .Concat(routineAfter)
            .Concat(viewAfter)
            .Concat(triggerAfter)
            .Concat(ruleAfter)
            .Concat(publicationAfter)
            .Concat(subscriptionAfter)
            .Concat(typeAfter)
            .Concat(collationAfter)
            .Concat(extensionAfter)
            .Concat(dropSchemas)
            .Concat(eventTriggerAfter)
            .Concat(tablespaceAfter)
            .ToArray();
        var ensuredSchemaNames = new HashSet<string>(StringComparer.Ordinal);
        return operations.Where(operation =>
                operation is not EnsureSchemaOperation ensureSchema || ensuredSchemaNames.Add(ensureSchema.Name))
            .ToArray();
    }

    private static IReadOnlyList<BlueTuskPropertyGraphDefinition> GetGraphs(IRelationalModel? model) =>
        model is null
            ? Array.Empty<BlueTuskPropertyGraphDefinition>()
            : BlueTuskPropertyGraphMetadata.Get(model.Model);

    private static IReadOnlyList<BlueTuskPartitionedTableDefinition> GetPartitions(IRelationalModel? model) =>
        BlueTuskPartitionMetadata.GetTables(model);

    private static bool PartitionDefinitionsEqual(
        IReadOnlyList<BlueTuskPartitionedTableDefinition> left,
        IReadOnlyList<BlueTuskPartitionedTableDefinition> right) =>
        string.Equals(
            BlueTuskPartitionMetadata.Serialize(left),
            BlueTuskPartitionMetadata.Serialize(right),
            StringComparison.Ordinal);

    private static void AddPartitionDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        IReadOnlyList<MigrationOperation> baseOperations,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        var source = GetPartitions(sourceModel).ToDictionary(PartitionTableKey.Create);
        var target = GetPartitions(targetModel).ToDictionary(PartitionTableKey.Create);
        var processedTargets = new HashSet<PartitionTableKey>();
        var ensuredSchemas = baseOperations.OfType<EnsureSchemaOperation>()
            .Select(operation => operation.Name)
            .Concat(source.Keys.Select(key => key.Schema))
            .Concat(source.Values.SelectMany(table => GetPartitionSchemas(table.Partitioning.Partitions)))
            .Where(schema => schema is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (key, sourceTable) in source)
        {
            var targetKey = key;
            if (!target.TryGetValue(targetKey, out var targetTable))
            {
                var rename = baseOperations.OfType<RenameTableOperation>().SingleOrDefault(
                    operation =>
                        string.Equals(operation.Name, key.Name, StringComparison.Ordinal) &&
                        string.Equals(operation.Schema, key.Schema, StringComparison.Ordinal));
                if (rename is null)
                {
                    continue;
                }

                var newName = rename.NewName ?? rename.Name;
                targetKey = new PartitionTableKey(rename.NewSchema ?? rename.Schema, newName);
                if (!target.TryGetValue(targetKey, out targetTable))
                {
                    var candidates = target
                        .Where(candidate =>
                            string.Equals(candidate.Key.Name, newName, StringComparison.Ordinal))
                        .ToArray();
                    if (candidates.Length != 1)
                    {
                        continue;
                    }

                    (targetKey, targetTable) = candidates[0];
                }
            }

            processedTargets.Add(targetKey);

            if (!PartitioningHeadEquals(sourceTable.Partitioning, targetTable.Partitioning))
            {
                throw new InvalidOperationException(
                    $"PostgreSQL cannot change partition strategy or keys for table '{targetKey.Schema}.{targetKey.Name}' in place. " +
                    "Create an explicit data-preserving replacement migration.");
            }

            AddChildDifferences(
                targetTable.Name,
                targetTable.Schema,
                sourceTable.Partitioning,
                targetTable.Partitioning,
                before,
                after,
                ensuredSchemas);
        }

        foreach (var (key, targetTable) in target)
        {
            if (!processedTargets.Contains(key))
            {
                AddCreateOperations(
                    targetTable.Name,
                    targetTable.Schema,
                    targetTable.Partitioning.Partitions,
                    after,
                    ensuredSchemas);
            }
        }
    }

    private static IEnumerable<string?> GetPartitionSchemas(
        IReadOnlyList<BlueTuskPartitionDefinition> partitions) =>
        partitions.SelectMany(
            partition => new[] { partition.Schema }.Concat(
                partition.Partitioning is null
                    ? Array.Empty<string?>()
                    : GetPartitionSchemas(partition.Partitioning.Partitions)));

    private static void AddChildDifferences(
        string parentName,
        string? parentSchema,
        BlueTuskPartitioningDefinition source,
        BlueTuskPartitioningDefinition target,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after,
        ISet<string> ensuredSchemas)
    {
        var sourceByName = source.Partitions.ToDictionary(PartitionKey.Create);
        var targetByName = target.Partitions.ToDictionary(PartitionKey.Create);
        var unmatchedTargets = new Dictionary<PartitionKey, BlueTuskPartitionDefinition>(targetByName);
        foreach (var (key, sourcePartition) in sourceByName)
        {
            if (targetByName.TryGetValue(key, out var targetPartition))
            {
                unmatchedTargets.Remove(key);
                if (BoundEquals(sourcePartition.Bound, targetPartition.Bound) &&
                    SubpartitioningHeadEquals(sourcePartition.Partitioning, targetPartition.Partitioning))
                {
                    if (sourcePartition.Partitioning is { } sourceSubpartitioning &&
                        targetPartition.Partitioning is { } targetSubpartitioning)
                    {
                        AddChildDifferences(
                            targetPartition.Name,
                            targetPartition.Schema,
                            sourceSubpartitioning,
                            targetSubpartitioning,
                            before,
                            after,
                            ensuredSchemas);
                    }

                    continue;
                }

                before.Add(CreateDrop(sourcePartition));
                AddCreateOperation(parentName, parentSchema, targetPartition, after, ensuredSchemas);
                continue;
            }

            var renameCandidates = unmatchedTargets.Values
                .Where(candidate => PartitionBodyEquals(sourcePartition, candidate))
                .ToArray();
            if (renameCandidates.Length == 1)
            {
                var renamed = renameCandidates[0];
                before.Add(new AlterBlueTuskPartitionOperation
                {
                    Name = sourcePartition.Name,
                    Schema = sourcePartition.Schema,
                    NewName = renamed.Name,
                    NewSchema = renamed.Schema,
                });
                unmatchedTargets.Remove(PartitionKey.Create(renamed));
            }
            else
            {
                before.Add(CreateDrop(sourcePartition));
            }
        }

        foreach (var targetPartition in unmatchedTargets.Values)
        {
            AddCreateOperation(parentName, parentSchema, targetPartition, after, ensuredSchemas);
        }
    }

    private static void AddCreateOperations(
        string parentName,
        string? parentSchema,
        IReadOnlyList<BlueTuskPartitionDefinition> partitions,
        ICollection<MigrationOperation> operations,
        ISet<string> ensuredSchemas)
    {
        foreach (var partition in partitions)
        {
            AddCreateOperation(parentName, parentSchema, partition, operations, ensuredSchemas);
        }
    }

    private static void AddCreateOperation(
        string parentName,
        string? parentSchema,
        BlueTuskPartitionDefinition partition,
        ICollection<MigrationOperation> operations,
        ISet<string> ensuredSchemas)
    {
        if (partition.Schema is { } schema && ensuredSchemas.Add(schema))
        {
            operations.Add(new EnsureSchemaOperation { Name = schema });
        }

        operations.Add(new CreateBlueTuskPartitionOperation
        {
            ParentName = parentName,
            ParentSchema = parentSchema,
            Definition = partition,
        });
        if (partition.Partitioning is { } subpartitioning)
        {
            AddCreateOperations(
                partition.Name,
                partition.Schema,
                subpartitioning.Partitions,
                operations,
                ensuredSchemas);
        }
    }

    private static DropBlueTuskPartitionOperation CreateDrop(BlueTuskPartitionDefinition partition) =>
        new()
        {
            Name = partition.Name,
            Schema = partition.Schema,
            IsDestructiveChange = true,
        };

    private static bool PartitioningHeadEquals(
        BlueTuskPartitioningDefinition left,
        BlueTuskPartitioningDefinition right) =>
        string.Equals(
            BlueTuskPartitionMetadata.Serialize(left with { Partitions = Array.Empty<BlueTuskPartitionDefinition>() }),
            BlueTuskPartitionMetadata.Serialize(right with { Partitions = Array.Empty<BlueTuskPartitionDefinition>() }),
            StringComparison.Ordinal);

    private static bool SubpartitioningHeadEquals(
        BlueTuskPartitioningDefinition? left,
        BlueTuskPartitioningDefinition? right) =>
        left is null || right is null
            ? left is null && right is null
            : PartitioningHeadEquals(left, right);

    private static bool BoundEquals(BlueTuskPartitionBound left, BlueTuskPartitionBound right) =>
        string.Equals(
            BlueTuskPartitionMetadata.Serialize(left),
            BlueTuskPartitionMetadata.Serialize(right),
            StringComparison.Ordinal);

    private static bool PartitionBodyEquals(
        BlueTuskPartitionDefinition left,
        BlueTuskPartitionDefinition right) =>
        BoundEquals(left.Bound, right.Bound) &&
        string.Equals(
            left.Partitioning is null ? null : BlueTuskPartitionMetadata.Serialize(left.Partitioning),
            right.Partitioning is null ? null : BlueTuskPartitionMetadata.Serialize(right.Partitioning),
            StringComparison.Ordinal);

    private static bool DefinitionsEqual(
        IReadOnlyList<BlueTuskPropertyGraphDefinition> left,
        IReadOnlyList<BlueTuskPropertyGraphDefinition> right) =>
        string.Equals(
            BlueTuskPropertyGraphMetadata.Serialize(left),
            BlueTuskPropertyGraphMetadata.Serialize(right),
            StringComparison.Ordinal);

    private static bool DefinitionEquals(
        BlueTuskPropertyGraphDefinition left,
        BlueTuskPropertyGraphDefinition right) =>
        string.Equals(
            BlueTuskPropertyGraphMetadata.Serialize(left),
            BlueTuskPropertyGraphMetadata.Serialize(right),
            StringComparison.Ordinal);

    private static bool DefinitionBodyEquals(
        BlueTuskPropertyGraphDefinition left,
        BlueTuskPropertyGraphDefinition right) =>
        DefinitionEquals(
            left with { Name = string.Empty, Schema = null },
            right with { Name = string.Empty, Schema = null });

    private static DropBlueTuskPropertyGraphOperation CreateDrop(
        BlueTuskPropertyGraphDefinition definition) =>
        new() { Name = definition.Name, Schema = definition.Schema };

    private static CreateBlueTuskPropertyGraphOperation CreateCreate(
        BlueTuskPropertyGraphDefinition definition) =>
        new() { Definition = definition };

    private readonly record struct GraphKey(string? Schema, string Name)
    {
        public static GraphKey Create(BlueTuskPropertyGraphDefinition definition) =>
            new(definition.Schema, definition.Name);
    }

    private readonly record struct PartitionTableKey(string? Schema, string Name)
    {
        public static PartitionTableKey Create(BlueTuskPartitionedTableDefinition definition) =>
            new(definition.Schema, definition.Name);
    }

    private readonly record struct PartitionKey(string? Schema, string Name)
    {
        public static PartitionKey Create(BlueTuskPartitionDefinition definition) =>
            new(definition.Schema, definition.Name);
    }
}

#pragma warning restore EF1001

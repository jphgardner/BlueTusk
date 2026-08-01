using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
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
        !DefinitionsEqual(GetGraphs(source), GetGraphs(target));

    public override IReadOnlyList<MigrationOperation> GetDifferences(
        IRelationalModel? source,
        IRelationalModel? target)
    {
        var baseOperations = base.GetDifferences(source, target);
        var sourceGraphs = GetGraphs(source);
        var targetGraphs = GetGraphs(target);
        var sourceByName = sourceGraphs.ToDictionary(GraphKey.Create);
        var targetByName = targetGraphs.ToDictionary(GraphKey.Create);
        var before = new List<MigrationOperation>();
        var after = new List<MigrationOperation>();

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

        return before.Concat(baseOperations).Concat(after).ToArray();
    }

    private static IReadOnlyList<BlueTuskPropertyGraphDefinition> GetGraphs(IRelationalModel? model) =>
        model is null
            ? Array.Empty<BlueTuskPropertyGraphDefinition>()
            : BlueTuskPropertyGraphMetadata.Get(model.Model);

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
}

#pragma warning restore EF1001

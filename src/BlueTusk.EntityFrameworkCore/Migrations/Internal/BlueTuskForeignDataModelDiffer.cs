using BlueTusk.EntityFrameworkCore.ForeignData;
using BlueTusk.EntityFrameworkCore.ForeignData.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskForeignDataModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        !string.Equals(
            BlueTuskForeignDataMetadata.Serialize(Get(source)),
            BlueTuskForeignDataMetadata.Serialize(Get(target)),
            StringComparison.Ordinal);

    public static void AddDifferences(
        IRelationalModel? sourceModel,
        IRelationalModel? targetModel,
        IReadOnlyList<MigrationOperation> baseOperations,
        ICollection<MigrationOperation> beforeRelational,
        ICollection<MigrationOperation> afterRelational)
    {
        EnrichColumnOperations(targetModel, baseOperations);
        var source = Get(sourceModel);
        var target = Get(targetModel);
        var wrapperAfter = new List<MigrationOperation>();
        var serverAfter = new List<MigrationOperation>();
        var mappingAfter = new List<MigrationOperation>();
        var wrapperRenames = AddWrapperDifferences(source.Wrappers, target.Wrappers, beforeRelational,
            wrapperAfter);
        var serverRenames = AddServerDifferences(source.Servers, target.Servers, wrapperRenames,
            beforeRelational, serverAfter);
        CanonicalizeForeignTableServerRenames(baseOperations, serverRenames);
        AddUserMappingDifferences(source.UserMappings, target.UserMappings, serverRenames,
            beforeRelational, mappingAfter);
        foreach (var operation in mappingAfter.Concat(serverAfter).Concat(wrapperAfter))
        {
            afterRelational.Add(operation);
        }
    }

    private static Dictionary<string, string> AddWrapperDifferences(
        IReadOnlyList<BlueTuskForeignDataWrapperDefinition> source,
        IReadOnlyList<BlueTuskForeignDataWrapperDefinition> target,
        ICollection<MigrationOperation> before,
        List<MigrationOperation> after)
    {
        var targets = target.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var unmatched = new Dictionary<string, BlueTuskForeignDataWrapperDefinition>(targets, StringComparer.Ordinal);
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var oldDefinition in source)
        {
            if (targets.TryGetValue(oldDefinition.Name, out var definition))
            {
                unmatched.Remove(definition.Name);
                if (!WrapperEquals(oldDefinition, definition))
                {
                    before.Add(new AlterForeignDataWrapperOperation
                    {
                        OldDefinition = oldDefinition,
                        Definition = definition,
                    });
                }

                continue;
            }

            var candidates = unmatched.Values.Where(candidate => WrapperBodyEquals(oldDefinition, candidate)).ToArray();
            if (candidates.Length == 1)
            {
                var renamed = candidates[0];
                before.Add(new RenameForeignDataWrapperOperation
                {
                    Name = oldDefinition.Name,
                    NewName = renamed.Name,
                });
                renames.Add(oldDefinition.Name, renamed.Name);
                unmatched.Remove(renamed.Name);
            }
            else
            {
                after.Add(new DropForeignDataWrapperOperation
                {
                    Name = oldDefinition.Name,
                    IsDestructiveChange = true,
                });
            }
        }

        foreach (var definition in unmatched.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            before.Add(new CreateForeignDataWrapperOperation { Definition = definition });
        }

        return renames;
    }

    private static Dictionary<string, string> AddServerDifferences(
        IReadOnlyList<BlueTuskForeignServerDefinition> source,
        IReadOnlyList<BlueTuskForeignServerDefinition> target,
        IReadOnlyDictionary<string, string> wrapperRenames,
        ICollection<MigrationOperation> before,
        List<MigrationOperation> after)
    {
        var targets = target.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var unmatched = new Dictionary<string, BlueTuskForeignServerDefinition>(targets, StringComparer.Ordinal);
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var original in source)
        {
            var oldDefinition = Canonicalize(original, wrapperRenames);
            if (targets.TryGetValue(oldDefinition.Name, out var definition))
            {
                unmatched.Remove(definition.Name);
                if (!ServerEquals(oldDefinition, definition))
                {
                    before.Add(new AlterForeignServerOperation
                    {
                        OldDefinition = oldDefinition,
                        Definition = definition,
                    });
                }

                continue;
            }

            var candidates = unmatched.Values.Where(candidate => ServerBodyEquals(oldDefinition, candidate)).ToArray();
            if (candidates.Length == 1)
            {
                var renamed = candidates[0];
                before.Add(new RenameForeignServerOperation
                {
                    Name = original.Name,
                    NewName = renamed.Name,
                });
                renames.Add(original.Name, renamed.Name);
                unmatched.Remove(renamed.Name);
            }
            else
            {
                after.Add(new DropForeignServerOperation
                {
                    Name = original.Name,
                    IsDestructiveChange = true,
                });
            }
        }

        foreach (var definition in unmatched.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            before.Add(new CreateForeignServerOperation { Definition = definition });
        }

        return renames;
    }

    private static void AddUserMappingDifferences(
        IReadOnlyList<BlueTuskUserMappingDefinition> source,
        IReadOnlyList<BlueTuskUserMappingDefinition> target,
        IReadOnlyDictionary<string, string> serverRenames,
        ICollection<MigrationOperation> before,
        List<MigrationOperation> after)
    {
        var targets = target.ToDictionary(MappingKey.Create);
        var unmatched = new Dictionary<MappingKey, BlueTuskUserMappingDefinition>(targets);
        foreach (var original in source)
        {
            var oldDefinition = Canonicalize(original, serverRenames);
            var key = MappingKey.Create(oldDefinition);
            if (targets.TryGetValue(key, out var definition))
            {
                unmatched.Remove(key);
                if (!MappingEquals(oldDefinition, definition))
                {
                    before.Add(new AlterUserMappingOperation
                    {
                        OldDefinition = oldDefinition,
                        Definition = definition,
                    });
                }

                continue;
            }

            after.Add(new DropUserMappingOperation
            {
                ServerName = oldDefinition.ServerName,
                UserName = oldDefinition.UserName,
                IsDestructiveChange = true,
            });
        }

        foreach (var definition in unmatched.Values
                     .OrderBy(item => item.ServerName, StringComparer.Ordinal)
                     .ThenBy(item => item.UserName, StringComparer.Ordinal))
        {
            before.Add(new CreateUserMappingOperation { Definition = definition });
        }
    }

    private static void EnrichColumnOperations(
        IRelationalModel? targetModel,
        IReadOnlyList<MigrationOperation> operations)
    {
        var tables = targetModel?.Tables.ToDictionary(table => (table.Schema, table.Name))
            ?? new Dictionary<(string? Schema, string Name), ITable>();
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case CreateTableOperation create when
                    create[BlueTuskForeignDataMetadata.ForeignTableAnnotationName] is string serialized:
                    var definition = BlueTuskForeignDataMetadata.DeserializeForeignTable(serialized);
                    foreach (var column in create.Columns)
                    {
                        AddColumnOptions(column, definition);
                    }

                    break;
                case AddColumnOperation add when tables.TryGetValue((add.Schema, add.Table), out var table):
                    var foreignTable = BlueTuskForeignDataMetadata.GetTableDefinition(table);
                    if (foreignTable is not null)
                    {
                        AddColumnOptions(add, foreignTable);
                    }

                    break;
            }
        }
    }

    private static void CanonicalizeForeignTableServerRenames(
        IReadOnlyList<MigrationOperation> operations,
        Dictionary<string, string> serverRenames)
    {
        foreach (var alter in operations.OfType<AlterTableOperation>())
        {
            if (alter.OldTable[BlueTuskForeignDataMetadata.ForeignTableAnnotationName] is not string serialized)
            {
                continue;
            }

            var definition = BlueTuskForeignDataMetadata.DeserializeForeignTable(serialized);
            if (serverRenames.TryGetValue(definition.ServerName, out var renamed))
            {
                alter.OldTable[BlueTuskForeignDataMetadata.ForeignTableAnnotationName] =
                    BlueTuskForeignDataMetadata.Serialize(definition with { ServerName = renamed });
            }
        }
    }

    private static void AddColumnOptions(ColumnOperation operation, BlueTuskForeignTableDefinition definition)
    {
        var options = definition.Columns.FirstOrDefault(column => column.Name == operation.Name)?.Options;
        if (options is { Count: > 0 })
        {
            operation[BlueTuskForeignDataMetadata.ForeignColumnOptionsAnnotationName] =
                BlueTuskForeignDataMetadata.SerializeOptions(options);
        }
    }

    private static BlueTuskForeignServerDefinition Canonicalize(
        BlueTuskForeignServerDefinition definition,
        IReadOnlyDictionary<string, string> wrapperRenames) =>
        wrapperRenames.TryGetValue(definition.ForeignDataWrapper, out var renamed)
            ? definition with { ForeignDataWrapper = renamed }
            : definition;

    private static BlueTuskUserMappingDefinition Canonicalize(
        BlueTuskUserMappingDefinition definition,
        IReadOnlyDictionary<string, string> serverRenames) =>
        serverRenames.TryGetValue(definition.ServerName, out var renamed)
            ? definition with { ServerName = renamed }
            : definition;

    private static bool WrapperEquals(
        BlueTuskForeignDataWrapperDefinition left,
        BlueTuskForeignDataWrapperDefinition right) =>
        BlueTuskForeignDataMetadata.Serialize(left) == BlueTuskForeignDataMetadata.Serialize(right);

    private static bool WrapperBodyEquals(
        BlueTuskForeignDataWrapperDefinition left,
        BlueTuskForeignDataWrapperDefinition right) => WrapperEquals(left with { Name = "_" }, right with { Name = "_" });

    private static bool ServerEquals(
        BlueTuskForeignServerDefinition left,
        BlueTuskForeignServerDefinition right) =>
        BlueTuskForeignDataMetadata.Serialize(left) == BlueTuskForeignDataMetadata.Serialize(right);

    private static bool ServerBodyEquals(
        BlueTuskForeignServerDefinition left,
        BlueTuskForeignServerDefinition right) => ServerEquals(left with { Name = "_" }, right with { Name = "_" });

    private static bool MappingEquals(
        BlueTuskUserMappingDefinition left,
        BlueTuskUserMappingDefinition right) =>
        left.OptionsRedacted || right.OptionsRedacted ||
        BlueTuskForeignDataMetadata.Serialize(left) == BlueTuskForeignDataMetadata.Serialize(right);

    private static BlueTuskForeignDataDefinitionSet Get(IRelationalModel? model) =>
        model is null ? BlueTuskForeignDataDefinitionSet.Empty : BlueTuskForeignDataMetadata.Get(model.Model);

    private readonly record struct MappingKey(string ServerName, string? UserName)
    {
        public static MappingKey Create(BlueTuskUserMappingDefinition definition) =>
            new(definition.ServerName, definition.UserName);
    }
}

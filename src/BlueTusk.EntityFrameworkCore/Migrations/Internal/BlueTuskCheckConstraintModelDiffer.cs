using BlueTusk.EntityFrameworkCore.CheckConstraints.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskCheckConstraintModelDiffer
{
    public static bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        GetChangedConstraints(source, target, []).Any();

    public static void AddDifferences(
        IRelationalModel? source,
        IRelationalModel? target,
        IReadOnlyList<MigrationOperation> baseOperations,
        ICollection<MigrationOperation> before,
        ICollection<MigrationOperation> after)
    {
        foreach (var changed in GetChangedConstraints(source, target, baseOperations))
        {
            if (BlueTuskCheckConstraintMetadata.IsNotValid(changed.Source) &&
                !BlueTuskCheckConstraintMetadata.IsNotValid(changed.Target) &&
                BlueTuskCheckConstraintMetadata.HasNoInherit(changed.Source) ==
                BlueTuskCheckConstraintMetadata.HasNoInherit(changed.Target) &&
                BlueTuskCheckConstraintMetadata.IsNotEnforced(changed.Source) ==
                BlueTuskCheckConstraintMetadata.IsNotEnforced(changed.Target))
            {
                after.Add(new ValidateCheckConstraintOperation
                {
                    Name = changed.Name,
                    Table = changed.TargetTable,
                    Schema = changed.TargetSchema,
                });
                continue;
            }

            before.Add(new DropCheckConstraintOperation
            {
                Name = changed.Name,
                Table = changed.SourceTable,
                Schema = changed.SourceSchema,
                IsDestructiveChange = true,
            });
            var add = new AddCheckConstraintOperation
            {
                Name = changed.Name,
                Table = changed.TargetTable,
                Schema = changed.TargetSchema,
                Sql = changed.Target.Sql,
            };
            CopyOptions(changed.Target, add);
            after.Add(add);
        }
    }

    private static IEnumerable<ChangedConstraint> GetChangedConstraints(
        IRelationalModel? source,
        IRelationalModel? target,
        IReadOnlyList<MigrationOperation> baseOperations)
    {
        if (source is null || target is null)
        {
            yield break;
        }

        var targetTables = target.Tables.ToDictionary(TableKey.Create);
        foreach (var sourceTable in source.Tables)
        {
            if (!TryFindTargetTable(sourceTable, targetTables, baseOperations, out var targetTable))
            {
                continue;
            }

            var targetConstraints = targetTable.CheckConstraints
                .Where(constraint => constraint.Name is not null)
                .ToDictionary(
                constraint => constraint.Name!,
                StringComparer.Ordinal);
            foreach (var sourceConstraint in sourceTable.CheckConstraints)
            {
                if (sourceConstraint.Name is not { } constraintName ||
                    !targetConstraints.TryGetValue(constraintName, out var targetConstraint) ||
                    !string.Equals(sourceConstraint.Sql, targetConstraint.Sql, StringComparison.Ordinal) ||
                    OptionsEqual(sourceConstraint, targetConstraint))
                {
                    continue;
                }

                yield return new ChangedConstraint(
                    sourceTable.Name,
                    sourceTable.Schema,
                    targetTable.Name,
                    targetTable.Schema,
                    constraintName,
                    sourceConstraint,
                    targetConstraint);
            }
        }
    }

    private static bool TryFindTargetTable(
        ITable sourceTable,
        IReadOnlyDictionary<TableKey, ITable> targetTables,
        IReadOnlyList<MigrationOperation> baseOperations,
        out ITable targetTable)
    {
        if (targetTables.TryGetValue(TableKey.Create(sourceTable), out targetTable!))
        {
            return true;
        }

        var rename = baseOperations.OfType<RenameTableOperation>().SingleOrDefault(operation =>
            string.Equals(operation.Name, sourceTable.Name, StringComparison.Ordinal) &&
            string.Equals(operation.Schema, sourceTable.Schema, StringComparison.Ordinal));
        return rename is not null && targetTables.TryGetValue(
            new TableKey(rename.NewSchema ?? rename.Schema, rename.NewName ?? rename.Name),
            out targetTable!);
    }

    private static bool OptionsEqual(ICheckConstraint source, ICheckConstraint target) =>
        BlueTuskCheckConstraintMetadata.IsNotValid(source) ==
        BlueTuskCheckConstraintMetadata.IsNotValid(target) &&
        BlueTuskCheckConstraintMetadata.HasNoInherit(source) ==
        BlueTuskCheckConstraintMetadata.HasNoInherit(target) &&
        BlueTuskCheckConstraintMetadata.IsNotEnforced(source) ==
        BlueTuskCheckConstraintMetadata.IsNotEnforced(target);

    private static void CopyOptions(ICheckConstraint source, AddCheckConstraintOperation target)
    {
        if (BlueTuskCheckConstraintMetadata.IsNotValid(source))
        {
            target[BlueTuskCheckConstraintMetadata.NotValidAnnotationName] = true;
        }

        if (BlueTuskCheckConstraintMetadata.HasNoInherit(source))
        {
            target[BlueTuskCheckConstraintMetadata.NoInheritAnnotationName] = true;
        }

        if (BlueTuskCheckConstraintMetadata.IsNotEnforced(source))
        {
            target[BlueTuskCheckConstraintMetadata.NotEnforcedAnnotationName] = true;
        }
    }

    private readonly record struct TableKey(string? Schema, string Name)
    {
        public static TableKey Create(ITable table) => new(table.Schema, table.Name);
    }

    private sealed record ChangedConstraint(
        string SourceTable,
        string? SourceSchema,
        string TargetTable,
        string? TargetSchema,
        string Name,
        ICheckConstraint Source,
        ICheckConstraint Target);
}

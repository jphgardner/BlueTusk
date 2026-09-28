using System.Collections.ObjectModel;

namespace BlueTusk.Schema;

public enum SchemaChangeKind
{
    RelationAdded,
    RelationRemoved,
    RelationKindChanged,
    SecurityChanged,
    ReplicaIdentityChanged,
    ColumnAdded,
    ColumnRemoved,
    ColumnTypeChanged,
    ColumnNullabilityChanged,
    ColumnOrdinalChanged,
    ColumnWriteBehaviorChanged,
    ColumnCollationChanged,
    ConstraintAdded,
    ConstraintRemoved,
    ConstraintChanged,
    IndexAdded,
    IndexRemoved,
    IndexChanged,
    PolicyAdded,
    PolicyRemoved,
    PolicyChanged,
    RelationDefinitionChanged,
}

public enum SchemaChangeImpact
{
    Informational,
    Additive,
    RequiresReview,
    Incompatible,
}

public sealed record SchemaChange(
    SchemaRelationIdentity Relation,
    SchemaChangeKind Kind,
    SchemaChangeImpact Impact,
    string? Member = null);

public sealed class SchemaComparison
{
    internal SchemaComparison(IEnumerable<SchemaChange> changes)
    {
        Changes = Array.AsReadOnly(changes.ToArray());
    }

    public ReadOnlyCollection<SchemaChange> Changes { get; }
    public bool HasIncompatibleChanges => Changes.Any(change => change.Impact == SchemaChangeImpact.Incompatible);
    public bool RequiresReview => Changes.Any(change => change.Impact >= SchemaChangeImpact.RequiresReview);
}

public sealed class SchemaConsumerContract
{
    public SchemaConsumerContract(string name, SchemaRelationIdentity relation, IEnumerable<string> columns, bool consumesWholeRow = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        var budget = new SchemaModelBudget(SchemaSnapshotLimits.Default);
        budget.Text(name, required: true);
        budget.Text(relation.Schema, required: true, identifier: true);
        budget.Text(relation.Name, required: true, identifier: true);
        Name = name;
        Relation = relation;
        Columns = Array.AsReadOnly(SchemaModelBudget.Materialize(columns, 32_767,
            column => budget.Text(column, required: true, identifier: true)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        ConsumesWholeRow = consumesWholeRow;
    }

    public string Name { get; }
    public SchemaRelationIdentity Relation { get; }
    public ReadOnlyCollection<string> Columns { get; }
    public bool ConsumesWholeRow { get; }
}

public sealed record SchemaConsumerImpact(string Consumer, ReadOnlyCollection<SchemaChange> Changes);

public static class SchemaCompatibility
{
    public static SchemaComparison Compare(SchemaSnapshot before, SchemaSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<SchemaChange>();
        if (before.Fingerprint == after.Fingerprint) { return new SchemaComparison(changes); }
        var previousIndex = 0;
        var currentIndex = 0;
        while (previousIndex < before.Relations.Count || currentIndex < after.Relations.Count)
        {
            if (previousIndex == before.Relations.Count)
            {
                changes.Add(new(after.Relations[currentIndex++].Identity, SchemaChangeKind.RelationAdded, SchemaChangeImpact.Additive));
                continue;
            }
            if (currentIndex == after.Relations.Count)
            {
                changes.Add(new(before.Relations[previousIndex++].Identity, SchemaChangeKind.RelationRemoved, SchemaChangeImpact.Incompatible));
                continue;
            }
            var relation = before.Relations[previousIndex];
            var next = after.Relations[currentIndex];
            var order = StringComparer.Ordinal.Compare(relation.Identity.Schema, next.Identity.Schema);
            if (order == 0) { order = StringComparer.Ordinal.Compare(relation.Identity.Name, next.Identity.Name); }
            if (order < 0)
            {
                changes.Add(new(relation.Identity, SchemaChangeKind.RelationRemoved, SchemaChangeImpact.Incompatible));
                previousIndex++;
            }
            else if (order > 0)
            {
                changes.Add(new(next.Identity, SchemaChangeKind.RelationAdded, SchemaChangeImpact.Additive));
                currentIndex++;
            }
            else { CompareRelation(relation, next, changes); previousIndex++; currentIndex++; }
        }

        return new SchemaComparison(changes);
    }

    public static ReadOnlyCollection<SchemaConsumerImpact> AnalyzeConsumers(
        SchemaComparison comparison,
        IEnumerable<SchemaConsumerContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(contracts);
        var changesByRelation = comparison.Changes.ToLookup(change => change.Relation);
        var impacts = new List<SchemaConsumerImpact>();
        var admittedContracts = 0;
        var admittedImpacts = 0;
        foreach (var contract in contracts)
        {
            if (++admittedContracts > 100_000) { throw new SchemaCaptureLimitException(); }
            if (contract is null) { throw new ArgumentException("Consumer contracts must not contain null members.", nameof(contracts)); }
            var columns = contract.Columns.ToHashSet(StringComparer.Ordinal);
            var changes = changesByRelation[contract.Relation].Where(change =>
                contract.ConsumesWholeRow || !IsColumnChange(change.Kind) ||
                change.Member is not null && columns.Contains(change.Member)).ToArray();
            if (changes.Length > 0)
            {
                admittedImpacts += changes.Length;
                if (admittedImpacts > 1_000_000) { throw new SchemaCaptureLimitException(); }
                impacts.Add(new(contract.Name, Array.AsReadOnly(changes)));
            }
        }

        return Array.AsReadOnly(impacts.ToArray());
    }

    private static bool IsColumnChange(SchemaChangeKind kind) => kind is >= SchemaChangeKind.ColumnAdded and <= SchemaChangeKind.ColumnCollationChanged;

    private static void CompareRelation(SchemaRelation before, SchemaRelation after, List<SchemaChange> changes)
    {
        if (ReferenceEquals(before, after)) { return; }
        if (before.Kind != after.Kind)
        {
            changes.Add(new(before.Identity, SchemaChangeKind.RelationKindChanged, SchemaChangeImpact.Incompatible));
        }

        if (before.RowSecurity != after.RowSecurity || before.ForceRowSecurity != after.ForceRowSecurity)
        {
            changes.Add(new(before.Identity, SchemaChangeKind.SecurityChanged, SchemaChangeImpact.RequiresReview));
        }

        if (before.ReplicaIdentity != after.ReplicaIdentity)
        {
            changes.Add(new(before.Identity, SchemaChangeKind.ReplicaIdentityChanged, SchemaChangeImpact.RequiresReview));
        }

        if (before.DefinitionSql != after.DefinitionSql)
        {
            changes.Add(new(before.Identity, SchemaChangeKind.RelationDefinitionChanged, SchemaChangeImpact.RequiresReview));
        }

        if (!EqualMembers(before.Columns, after.Columns))
        {
            var oldColumns = before.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
            var newColumns = after.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
            foreach (var column in before.Columns)
            {
                if (!newColumns.TryGetValue(column.Name, out var next))
                {
                    changes.Add(new(before.Identity, SchemaChangeKind.ColumnRemoved, SchemaChangeImpact.Incompatible, column.Name));
                    continue;
                }

                if (column.PostgreSqlType != next.PostgreSqlType)
                {
                    changes.Add(new(before.Identity, SchemaChangeKind.ColumnTypeChanged, SchemaChangeImpact.Incompatible, column.Name));
                }

                if (column.IsNullable != next.IsNullable)
                {
                    changes.Add(new(before.Identity, SchemaChangeKind.ColumnNullabilityChanged, SchemaChangeImpact.Incompatible, column.Name));
                }

                if (column.Ordinal != next.Ordinal)
                {
                    changes.Add(new(before.Identity, SchemaChangeKind.ColumnOrdinalChanged, SchemaChangeImpact.RequiresReview, column.Name));
                }

                if (column.DefaultSql != next.DefaultSql || column.IdentityKind != next.IdentityKind || column.GeneratedKind != next.GeneratedKind)
                {
                    changes.Add(new(before.Identity, SchemaChangeKind.ColumnWriteBehaviorChanged, SchemaChangeImpact.RequiresReview, column.Name));
                }

                if (column.Collation != next.Collation)
                {
                    changes.Add(new(before.Identity, SchemaChangeKind.ColumnCollationChanged, SchemaChangeImpact.RequiresReview, column.Name));
                }
            }

            foreach (var column in after.Columns)
            {
                if (!oldColumns.ContainsKey(column.Name))
                {
                    var impact = column.IsNullable || column.DefaultSql is not null || column.IdentityKind.Length != 0 || column.GeneratedKind.Length != 0
                        ? SchemaChangeImpact.Additive : SchemaChangeImpact.Incompatible;
                    changes.Add(new(before.Identity, SchemaChangeKind.ColumnAdded, impact, column.Name));
                }
            }
        }

        CompareMembers(before.Identity, before.Constraints, after.Constraints, item => item.Name,
            SchemaChangeKind.ConstraintAdded, SchemaChangeKind.ConstraintRemoved, SchemaChangeKind.ConstraintChanged,
            SchemaChangeImpact.RequiresReview, changes);
        CompareMembers(before.Identity, before.Indexes, after.Indexes, item => item.Name,
            SchemaChangeKind.IndexAdded, SchemaChangeKind.IndexRemoved, SchemaChangeKind.IndexChanged,
            SchemaChangeImpact.Informational, changes);
        CompareMembers(before.Identity, before.Policies, after.Policies, item => item.Name,
            SchemaChangeKind.PolicyAdded, SchemaChangeKind.PolicyRemoved, SchemaChangeKind.PolicyChanged,
            SchemaChangeImpact.RequiresReview, changes);
    }

    private static void CompareMembers<T>(SchemaRelationIdentity identity, IReadOnlyList<T> before, IReadOnlyList<T> after,
        Func<T, string> name, SchemaChangeKind added, SchemaChangeKind removed, SchemaChangeKind changed,
        SchemaChangeImpact impact, List<SchemaChange> changes)
    {
        if (EqualMembers(before, after)) { return; }
        var oldMembers = before.ToDictionary(name, StringComparer.Ordinal);
        var newMembers = after.ToDictionary(name, StringComparer.Ordinal);
        foreach (var (key, value) in oldMembers)
        {
            if (!newMembers.TryGetValue(key, out var next))
            {
                changes.Add(new(identity, removed, impact, key));
            }
            else if (!EqualityComparer<T>.Default.Equals(value, next))
            {
                changes.Add(new(identity, changed, impact, key));
            }
        }

        foreach (var key in newMembers.Keys)
        {
            if (!oldMembers.ContainsKey(key))
            {
                changes.Add(new(identity, added, impact, key));
            }
        }
    }

    private static bool EqualMembers<T>(IReadOnlyList<T> before, IReadOnlyList<T> after)
    {
        if (before.Count != after.Count) { return false; }
        for (var index = 0; index < before.Count; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(before[index], after[index])) { return false; }
        }
        return true;
    }
}

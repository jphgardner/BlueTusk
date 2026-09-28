using System.Collections.ObjectModel;

namespace BlueTusk.Schema;

public enum SchemaCatalogChangeKind { Added, Removed, Changed }

public sealed record SchemaCatalogChange(string ContractKind, SchemaRelationIdentity Identity, SchemaCatalogChangeKind Kind,
    SchemaChangeImpact Impact, string? Member = null);

public sealed class SchemaCatalogComparison
{
    internal SchemaCatalogComparison(SchemaComparison relations, IEnumerable<SchemaCatalogChange> changes)
    { Relations = relations; Changes = Array.AsReadOnly(changes.ToArray()); }
    public SchemaComparison Relations { get; }
    public ReadOnlyCollection<SchemaCatalogChange> Changes { get; }
    public bool HasIncompatibleChanges => Relations.HasIncompatibleChanges || Changes.Any(value => value.Impact == SchemaChangeImpact.Incompatible);
    public bool RequiresReview => Relations.RequiresReview || Changes.Any(value => value.Impact >= SchemaChangeImpact.RequiresReview);
}

public static class SchemaCatalogCompatibility
{
    public static SchemaCatalogComparison Compare(SchemaCatalogSnapshot before, SchemaCatalogSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        var changes = new List<SchemaCatalogChange>();
        void CompareMembers<T>(IReadOnlyList<T> oldValues, IReadOnlyList<T> newValues, Func<T, string> key,
            Func<T, SchemaRelationIdentity> identity, string category, Func<T, T, bool> equal,
            Func<T, T, SchemaChangeImpact> changed, SchemaChangeImpact added, Func<T, string?>? member = null)
        {
            var oldIndex = 0; var newIndex = 0;
            while (oldIndex < oldValues.Count || newIndex < newValues.Count)
            {
                var comparison = oldIndex == oldValues.Count ? 1 : newIndex == newValues.Count ? -1 :
                    StringComparer.Ordinal.Compare(key(oldValues[oldIndex]), key(newValues[newIndex]));
                if (comparison < 0)
                {
                    var value = oldValues[oldIndex++];
                    changes.Add(new(category, identity(value), SchemaCatalogChangeKind.Removed, SchemaChangeImpact.Incompatible, member?.Invoke(value)));
                }
                else if (comparison > 0)
                {
                    var value = newValues[newIndex++];
                    changes.Add(new(category, identity(value), SchemaCatalogChangeKind.Added, added, member?.Invoke(value)));
                }
                else
                {
                    var oldValue = oldValues[oldIndex++]; var newValue = newValues[newIndex++];
                    if (identity(oldValue) != identity(newValue))
                    {
                        changes.Add(new(category, identity(oldValue), SchemaCatalogChangeKind.Removed, SchemaChangeImpact.Incompatible, member?.Invoke(oldValue)));
                        changes.Add(new(category, identity(newValue), SchemaCatalogChangeKind.Added, added, member?.Invoke(newValue)));
                    }
                    else if (!equal(oldValue, newValue))
                    { changes.Add(new(category, identity(newValue), SchemaCatalogChangeKind.Changed, changed(oldValue, newValue), member?.Invoke(newValue))); }
                }
            }
        }
        CompareMembers(before.Types, after.Types, value => CatalogValidation.Key(value.Identity), value => value.Identity, "type",
            TypeEqual, TypeImpact, SchemaChangeImpact.Additive);
        CompareMembers(before.Routines, after.Routines, value => CatalogValidation.Key(value.Identity) + "\0" + value.IdentityArguments,
            value => value.Identity, "routine", (left, right) => left == right,
            (left, right) => left.Kind != right.Kind || left.ResultType != right.ResultType ? SchemaChangeImpact.Incompatible : SchemaChangeImpact.RequiresReview,
            SchemaChangeImpact.Additive, value => value.IdentityArguments);
        CompareMembers(before.Privileges, after.Privileges, CatalogValidation.PrivilegeKey, value => value.Identity, "privilege",
            (left, right) => left == right, (_, _) => SchemaChangeImpact.RequiresReview, SchemaChangeImpact.RequiresReview,
            value => value.ObjectKind + ":" + value.Privilege + ":" + value.Grantee);
        CompareMembers(before.Publications, after.Publications, value => value.Publication + "\0" + CatalogValidation.Key(value.Relation),
            value => value.Relation, "publication", (left, right) => left == right, (_, _) => SchemaChangeImpact.RequiresReview,
            SchemaChangeImpact.RequiresReview, value => value.Publication);
        CompareMembers(before.Extensions, after.Extensions, value => value.Name, value => new(value.Schema, value.Name), "extension",
            (left, right) => left == right, (_, _) => SchemaChangeImpact.RequiresReview, SchemaChangeImpact.RequiresReview);
        return new(SchemaCompatibility.Compare(before.Relations, after.Relations), changes);
    }

    private static bool TypeEqual(SchemaTypeContract left, SchemaTypeContract right) => left.Kind == right.Kind &&
        left.BaseType == right.BaseType && left.IsNullable == right.IsNullable && left.DefaultSql == right.DefaultSql &&
        left.Collation == right.Collation && left.EnumLabels.SequenceEqual(right.EnumLabels, StringComparer.Ordinal) &&
        left.Constraints.SequenceEqual(right.Constraints);

    private static SchemaChangeImpact TypeImpact(SchemaTypeContract left, SchemaTypeContract right)
    {
        if (left.Kind != right.Kind || left.BaseType != right.BaseType || left.IsNullable != right.IsNullable)
        { return SchemaChangeImpact.Incompatible; }
        if (left.Kind == "e")
        {
            var labels = left.EnumLabels.ToHashSet(StringComparer.Ordinal);
            if (!right.EnumLabels.Where(labels.Contains).SequenceEqual(left.EnumLabels, StringComparer.Ordinal))
            { return SchemaChangeImpact.Incompatible; }
        }
        return SchemaChangeImpact.RequiresReview;
    }
}

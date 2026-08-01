using BlueTusk.EntityFrameworkCore.UserDefinedTypes;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes.Internal;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskUserDefinedTypeAlterationPlanner
{
    public static IReadOnlyList<EnumValueChange> PlanEnum(
        BlueTuskEnumTypeDefinition oldDefinition,
        BlueTuskEnumTypeDefinition definition)
    {
        BlueTuskUserDefinedTypeMetadata.Validate(oldDefinition);
        BlueTuskUserDefinedTypeMetadata.Validate(definition);
        var current = oldDefinition.Labels.ToList();
        var changes = new List<EnumValueChange>();
        if (current.Count == definition.Labels.Count)
        {
            var mismatches = current.Select((value, index) => (value, index))
                .Where(item => !string.Equals(item.value, definition.Labels[item.index], StringComparison.Ordinal))
                .ToArray();
            if (mismatches.Length == 1)
            {
                var mismatch = mismatches[0];
                var renamed = definition.Labels[mismatch.index];
                changes.Add(new EnumValueChange(EnumValueChangeKind.Rename, mismatch.value, renamed));
                current[mismatch.index] = renamed;
            }
        }

        if (!IsSubsequence(current, definition.Labels))
        {
            throw new InvalidOperationException(
                $"PostgreSQL cannot remove or reorder enum labels for type '{definition.Schema}.{definition.Name}' in place. " +
                "Split unambiguous label renames from other changes or create an explicit data-preserving replacement migration.");
        }

        for (var index = 0; index < definition.Labels.Count; index++)
        {
            var label = definition.Labels[index];
            if (current.Contains(label, StringComparer.Ordinal))
            {
                continue;
            }

            var neighbor = definition.Labels.Skip(index + 1)
                .FirstOrDefault(candidate => current.Contains(candidate, StringComparer.Ordinal));
            changes.Add(new EnumValueChange(
                EnumValueChangeKind.Add,
                label,
                Neighbor: neighbor,
                Before: neighbor is not null));
            if (neighbor is null)
            {
                current.Add(label);
            }
            else
            {
                current.Insert(current.FindIndex(value => string.Equals(value, neighbor, StringComparison.Ordinal)), label);
            }
        }

        return changes;
    }

    public static void ValidateDomain(
        BlueTuskDomainTypeDefinition oldDefinition,
        BlueTuskDomainTypeDefinition definition)
    {
        BlueTuskUserDefinedTypeMetadata.Validate(oldDefinition);
        BlueTuskUserDefinedTypeMetadata.Validate(definition);
        if (!string.Equals(oldDefinition.BaseStoreType, definition.BaseStoreType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(oldDefinition.Collation, definition.Collation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"PostgreSQL cannot change the base store type or collation of domain '{definition.Schema}.{definition.Name}' in place. " +
                "Create an explicit data-preserving replacement migration.");
        }
    }

    public static IReadOnlyList<CompositeAttributeChange> PlanComposite(
        BlueTuskCompositeTypeDefinition oldDefinition,
        BlueTuskCompositeTypeDefinition definition)
    {
        BlueTuskUserDefinedTypeMetadata.Validate(oldDefinition);
        BlueTuskUserDefinedTypeMetadata.Validate(definition);
        var oldByName = oldDefinition.Attributes.ToDictionary(attribute => attribute.Name, StringComparer.Ordinal);
        var newByName = definition.Attributes.ToDictionary(attribute => attribute.Name, StringComparer.Ordinal);
        var mappedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedTargets = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<CompositeAttributeChange>();

        foreach (var attribute in oldDefinition.Attributes)
        {
            if (newByName.ContainsKey(attribute.Name))
            {
                mappedNames[attribute.Name] = attribute.Name;
                usedTargets.Add(attribute.Name);
                continue;
            }

            var renameCandidates = definition.Attributes
                .Where(candidate =>
                    !oldByName.ContainsKey(candidate.Name) &&
                    !usedTargets.Contains(candidate.Name) &&
                    AttributeBodyEquals(attribute, candidate))
                .ToArray();
            if (renameCandidates.Length == 1)
            {
                var renamed = renameCandidates[0];
                mappedNames[attribute.Name] = renamed.Name;
                usedTargets.Add(renamed.Name);
                changes.Add(new CompositeAttributeChange(
                    CompositeAttributeChangeKind.Rename,
                    attribute.Name,
                    renamed));
            }
        }

        var retained = oldDefinition.Attributes
            .Where(attribute => mappedNames.ContainsKey(attribute.Name))
            .Select(attribute => mappedNames[attribute.Name])
            .ToArray();
        var retainedSet = retained.ToHashSet(StringComparer.Ordinal);
        if (!definition.Attributes.Take(retained.Length)
                .Select(attribute => attribute.Name)
                .SequenceEqual(retained, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"PostgreSQL cannot reorder existing attributes or insert attributes before them in composite type " +
                $"'{definition.Schema}.{definition.Name}' in place. Create an explicit data-preserving replacement migration.");
        }

        foreach (var attribute in oldDefinition.Attributes)
        {
            if (!mappedNames.TryGetValue(attribute.Name, out var targetName))
            {
                changes.Add(new CompositeAttributeChange(
                    CompositeAttributeChangeKind.Drop,
                    attribute.Name));
                continue;
            }

            var target = newByName[targetName];
            if (!AttributeBodyEquals(attribute, target))
            {
                changes.Add(new CompositeAttributeChange(
                    CompositeAttributeChangeKind.Alter,
                    targetName,
                    target));
            }
        }

        foreach (var attribute in definition.Attributes.Where(attribute => !retainedSet.Contains(attribute.Name)))
        {
            changes.Add(new CompositeAttributeChange(
                CompositeAttributeChangeKind.Add,
                attribute.Name,
                attribute));
        }

        return changes;
    }

    private static bool IsSubsequence(IReadOnlyList<string> current, IReadOnlyList<string> target)
    {
        var targetIndex = 0;
        foreach (var value in current)
        {
            while (targetIndex < target.Count &&
                   !string.Equals(target[targetIndex], value, StringComparison.Ordinal))
            {
                targetIndex++;
            }

            if (targetIndex == target.Count)
            {
                return false;
            }

            targetIndex++;
        }

        return true;
    }

    private static bool AttributeBodyEquals(
        BlueTuskCompositeAttributeDefinition left,
        BlueTuskCompositeAttributeDefinition right) =>
        string.Equals(left.StoreType, right.StoreType, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Collation, right.Collation, StringComparison.Ordinal);
}

internal enum EnumValueChangeKind
{
    Add,
    Rename,
}

internal sealed record EnumValueChange(
    EnumValueChangeKind Kind,
    string Value,
    string? NewValue = null,
    string? Neighbor = null,
    bool Before = false);

internal enum CompositeAttributeChangeKind
{
    Add,
    Drop,
    Rename,
    Alter,
}

internal sealed record CompositeAttributeChange(
    CompositeAttributeChangeKind Kind,
    string Name,
    BlueTuskCompositeAttributeDefinition? Attribute = null);

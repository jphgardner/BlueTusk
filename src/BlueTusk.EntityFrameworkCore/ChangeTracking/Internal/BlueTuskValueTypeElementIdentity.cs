#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Collections;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;

// EF Core diffs a complex collection by element reference. A value-type element has no identity, so its current and
// original elements are paired here and each pair is given one shared key object; EF Core's diff then runs over the
// keys. Unchanged elements are paired first (at their original position, then anywhere) and act as anchors. A changed
// element is paired only with an unpaired original element between the same anchors, best match first, so an element
// edited in place is reported as modified while inserted and removed elements are reported as added and deleted.
// Values are compared through the model: scalar properties with their value comparers, complex values recursively.
internal static class BlueTuskValueTypeElementIdentity
{
    public static (IList? Current, IList? Original) Create(IComplexType elementType, IList? current, IList? original)
    {
        var originalKeys = CreateKeys(original);
        if (current is null || original is null)
        {
            return (current is null ? null : CreateKeys(current), original is null ? null : originalKeys);
        }

        var currentKeys = new object?[current.Count];
        var pairedOriginal = new int[current.Count];
        Array.Fill(pairedOriginal, -1);
        var paired = new bool[originalKeys.Length];

        for (var i = 0; i < Math.Min(currentKeys.Length, originalKeys.Length); i++)
        {
            if (current[i] is { } element && original[i] is { } originalElement && ValueEquals(elementType, element, originalElement))
            {
                Pair(i, i);
            }
        }

        for (var i = 0; i < currentKeys.Length; i++)
        {
            if (pairedOriginal[i] >= 0 || current[i] is not { } element)
            {
                continue;
            }

            for (var j = 0; j < originalKeys.Length; j++)
            {
                if (!paired[j] && original[j] is { } originalElement && ValueEquals(elementType, element, originalElement))
                {
                    Pair(i, j);
                    break;
                }
            }
        }

        PairChangedElements(elementType, current, original, pairedOriginal, paired, Pair);

        for (var i = 0; i < currentKeys.Length; i++)
        {
            if (current[i] is not null && currentKeys[i] is null)
            {
                currentKeys[i] = new object();
            }
        }

        return (currentKeys, originalKeys);

        void Pair(int currentOrdinal, int originalOrdinal)
        {
            currentKeys[currentOrdinal] = originalKeys[originalOrdinal];
            pairedOriginal[currentOrdinal] = originalOrdinal;
            paired[originalOrdinal] = true;
        }
    }

    private static void PairChangedElements(
        IComplexType elementType,
        IList current,
        IList original,
        int[] pairedOriginal,
        bool[] paired,
        Action<int, int> pair)
    {
        // The anchors before and after each run of unpaired elements bound the original elements it may pair with.
        var lowerBounds = new int[current.Count];
        var lower = -1;
        for (var i = 0; i < current.Count; i++)
        {
            lowerBounds[i] = lower;
            lower = Math.Max(lower, pairedOriginal[i]);
        }

        var upper = original.Count;
        var runEnd = current.Count;
        for (var i = current.Count - 1; i >= -1; i--)
        {
            if (i >= 0 && pairedOriginal[i] < 0)
            {
                continue;
            }

            var runStart = i + 1;
            if (runStart < runEnd)
            {
                PairRun(runStart, runEnd, lowerBounds[runStart], upper);
            }

            if (i >= 0)
            {
                upper = Math.Min(upper, pairedOriginal[i]);
                runEnd = i;
            }
        }

        void PairRun(int start, int end, int lowerBound, int upperBound)
        {
            var candidates = new List<(int Current, int Original, int Score, int Distance)>();
            for (var i = start; i < end; i++)
            {
                if (current[i] is not { } element)
                {
                    continue;
                }

                for (var j = lowerBound + 1; j < upperBound; j++)
                {
                    if (!paired[j] && original[j] is { } originalElement)
                    {
                        candidates.Add((i, j, Similarity(elementType, element, originalElement), Math.Abs(i - start - (j - lowerBound - 1))));
                    }
                }
            }

            foreach (var (i, j, _, _) in candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Distance)
                .ThenBy(c => c.Current)
                .ThenBy(c => c.Original))
            {
                if (pairedOriginal[i] < 0 && !paired[j])
                {
                    pair(i, j);
                }
            }
        }
    }

    private static object?[] CreateKeys(IList? elements)
    {
        var keys = new object?[elements?.Count ?? 0];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = elements![i] is null ? null : new object();
        }

        return keys;
    }

    // The number of the element's scalar properties and complex values that are unchanged.
    private static int Similarity(IComplexType complexType, object left, object right)
    {
        var score = 0;
        foreach (var property in complexType.GetProperties())
        {
            if (!property.IsShadowProperty() && PropertyEquals(property, left, right))
            {
                score++;
            }
        }

        foreach (var complexProperty in complexType.GetComplexProperties())
        {
            if (!complexProperty.IsShadowProperty() && ComplexPropertyEquals(complexProperty, left, right))
            {
                score++;
            }
        }

        return score;
    }

    private static bool ValueEquals(IComplexType complexType, object left, object right)
        => complexType.GetProperties().All(p => p.IsShadowProperty() || PropertyEquals(p, left, right))
            && complexType.GetComplexProperties().All(p => p.IsShadowProperty() || ComplexPropertyEquals(p, left, right));

    private static bool PropertyEquals(IProperty property, object left, object right)
        => property.GetValueComparer().Equals(property.GetGetter().GetClrValue(left), property.GetGetter().GetClrValue(right));

    private static bool ComplexPropertyEquals(IComplexProperty complexProperty, object left, object right)
    {
        var leftValue = complexProperty.GetGetter().GetClrValue(left);
        var rightValue = complexProperty.GetGetter().GetClrValue(right);
        if (leftValue is null || rightValue is null)
        {
            return leftValue is null && rightValue is null;
        }

        if (!complexProperty.IsCollection)
        {
            return ValueEquals(complexProperty.ComplexType, leftValue, rightValue);
        }

        var leftElements = (IList)leftValue;
        var rightElements = (IList)rightValue;
        if (leftElements.Count != rightElements.Count)
        {
            return false;
        }

        for (var i = 0; i < leftElements.Count; i++)
        {
            if (leftElements[i] is not { } leftElement || rightElements[i] is not { } rightElement)
            {
                if (leftElements[i] is not null || rightElements[i] is not null)
                {
                    return false;
                }

                continue;
            }

            if (!ValueEquals(complexProperty.ComplexType, leftElement, rightElement))
            {
                return false;
            }
        }

        return true;
    }
}

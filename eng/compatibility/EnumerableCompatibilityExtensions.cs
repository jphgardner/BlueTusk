namespace BlueTusk.Compatibility;

internal static class EnumerableCompatibilityExtensions
{
#if !NET7_0_OR_GREATER
    public static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source, IComparer<T>? comparer = null) =>
        source.OrderBy(static value => value, comparer);
#endif

#if !NET6_0_OR_GREATER
    [return: System.Diagnostics.CodeAnalysis.MaybeNull]
    public static TSource MinBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> selector)
    {
        BlueTuskGuard.NotNull(source);
        BlueTuskGuard.NotNull(selector);
        using var enumerator = source.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            if (default(TSource) is null)
            {
                return default;
            }

            throw new InvalidOperationException("The sequence contains no elements.");
        }

        var minimum = enumerator.Current;
        var minimumKey = selector(minimum);
        while (enumerator.MoveNext())
        {
            var candidate = enumerator.Current;
            var key = selector(candidate);
            if (key is not null && (minimumKey is null || Comparer<TKey>.Default.Compare(key, minimumKey) < 0))
            {
                minimum = candidate;
                minimumKey = key;
            }
        }

        return minimum;
    }

    public static IEnumerable<T[]> Chunk<T>(this IEnumerable<T> source, int size)
    {
        BlueTuskGuard.NotNull(source);
        BlueTuskGuard.NotNegativeOrZero(size);
        return Enumerate(source, size);
    }

    private static IEnumerable<T[]> Enumerate<T>(IEnumerable<T> source, int size)
    {
        using var enumerator = source.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var chunk = new T[size];
            chunk[0] = enumerator.Current;
            var count = 1;
            while (count < size && enumerator.MoveNext())
            {
                chunk[count++] = enumerator.Current;
            }

            if (count != size)
            {
                Array.Resize(ref chunk, count);
            }

            yield return chunk;
        }
    }
#endif
}

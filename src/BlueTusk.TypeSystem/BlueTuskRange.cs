using System.Collections;

namespace BlueTusk.TypeSystem;

/// <summary>Represents one finite or unbounded PostgreSQL range boundary.</summary>
public readonly record struct BlueTuskRangeBound<T>
{
    private readonly T? _value;

    internal BlueTuskRangeBound(T value, bool isInclusive)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        _value = value;
        HasValue = true;
        IsInclusive = isInclusive;
    }

    public bool HasValue { get; }

    public bool IsInfinite => !HasValue;

    public bool IsInclusive { get; }

    public T Value => HasValue
        ? _value!
        : throw new InvalidOperationException("An unbounded PostgreSQL range boundary has no CLR value.");
}

public static class BlueTuskRangeBound
{
    public static BlueTuskRangeBound<T> Unbounded<T>() => default;

    public static BlueTuskRangeBound<T> Inclusive<T>(T value) =>
        new(value, isInclusive: true);

    public static BlueTuskRangeBound<T> Exclusive<T>(T value) =>
        new(value, isInclusive: false);
}

/// <summary>Represents a PostgreSQL range, including empty and unbounded values.</summary>
public readonly record struct BlueTuskRange<T>
{
    internal BlueTuskRange(bool isEmpty)
    {
        IsEmpty = isEmpty;
        LowerBound = default;
        UpperBound = default;
    }

    public BlueTuskRange(
        BlueTuskRangeBound<T> lowerBound,
        BlueTuskRangeBound<T> upperBound)
    {
        IsEmpty = false;
        LowerBound = lowerBound;
        UpperBound = upperBound;
    }

    public BlueTuskRange(T lowerBound, T upperBound)
        : this(
            BlueTuskRangeBound.Inclusive(lowerBound),
            BlueTuskRangeBound.Exclusive(upperBound))
    {
    }

    public BlueTuskRange(
        T lowerBound,
        bool lowerBoundInclusive,
        T upperBound,
        bool upperBoundInclusive)
        : this(
            lowerBoundInclusive
                ? BlueTuskRangeBound.Inclusive(lowerBound)
                : BlueTuskRangeBound.Exclusive(lowerBound),
            upperBoundInclusive
                ? BlueTuskRangeBound.Inclusive(upperBound)
                : BlueTuskRangeBound.Exclusive(upperBound))
    {
    }

    public bool IsEmpty { get; }

    public BlueTuskRangeBound<T> LowerBound { get; }

    public BlueTuskRangeBound<T> UpperBound { get; }
}

public static class BlueTuskRange
{
    public static BlueTuskRange<T> Empty<T>() => new(isEmpty: true);

    public static BlueTuskRange<T> Unbounded<T>() => default;
}

/// <summary>An immutable ordered collection of PostgreSQL ranges.</summary>
public sealed class BlueTuskMultirange<T> :
    IReadOnlyList<BlueTuskRange<T>>,
    IEquatable<BlueTuskMultirange<T>>
{
    private readonly BlueTuskRange<T>[] _ranges;

    public BlueTuskMultirange(IEnumerable<BlueTuskRange<T>> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        _ranges = ranges.ToArray();
    }

    public int Count => _ranges.Length;

    public BlueTuskRange<T> this[int index] => _ranges[index];

    public bool Equals(BlueTuskMultirange<T>? other) =>
        other is not null && _ranges.AsSpan().SequenceEqual(other._ranges);

    public override bool Equals(object? obj) =>
        obj is BlueTuskMultirange<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var range in _ranges)
        {
            hash.Add(range);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<BlueTuskRange<T>> GetEnumerator() =>
        ((IEnumerable<BlueTuskRange<T>>)_ranges).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _ranges.GetEnumerator();
}

public static class BlueTuskMultirange
{
    public static BlueTuskMultirange<T> Empty<T>() => new([]);
}

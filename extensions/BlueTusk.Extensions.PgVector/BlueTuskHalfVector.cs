using System.Collections;
using System.Globalization;
using System.Text;

namespace BlueTusk.Extensions.PgVector;

/// <summary>An immutable half-precision vector for PostgreSQL pgvector.</summary>
public sealed class BlueTuskHalfVector : IReadOnlyList<Half>, IEquatable<BlueTuskHalfVector>
{
    public const int MaxDimensions = 16_000;

    private readonly Half[] _values;

    public BlueTuskHalfVector(params Half[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Validate(values);
        _values = values.ToArray();
    }

    public int Count => _values.Length;

    public Half this[int index] => _values[index];

    public ReadOnlySpan<Half> AsSpan() => _values;

    public static BlueTuskHalfVector FromSinglePrecision(params float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var converted = new Half[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            if (!float.IsFinite(values[index]))
            {
                throw new ArgumentOutOfRangeException(nameof(values), "halfvec elements must be finite.");
            }

            converted[index] = (Half)values[index];
            if (!Half.IsFinite(converted[index]))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values),
                    "A halfvec element is outside the finite binary16 range.");
            }
        }

        return new BlueTuskHalfVector(converted);
    }

    public static BlueTuskHalfVector Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var dense = BlueTuskVector.Parse(value);
        return FromSinglePrecision(dense.ToArray());
    }

    public bool Equals(BlueTuskHalfVector? other) =>
        other is not null && _values.AsSpan().SequenceEqual(other._values);

    public override bool Equals(object? obj) => obj is BlueTuskHalfVector other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in _values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var builder = new StringBuilder().Append('[');
        for (var index = 0; index < _values.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(((float)_values[index]).ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.Append(']').ToString();
    }

    public IEnumerator<Half> GetEnumerator() => ((IEnumerable<Half>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();

    private static void Validate(ReadOnlySpan<Half> values)
    {
        if (values.IsEmpty)
        {
            throw new ArgumentException("A halfvec value must contain at least one dimension.", nameof(values));
        }

        if (values.Length > MaxDimensions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                $"A halfvec value cannot contain more than {MaxDimensions} dimensions.");
        }

        foreach (var value in values)
        {
            if (!Half.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(values), "halfvec elements must be finite.");
            }
        }
    }
}

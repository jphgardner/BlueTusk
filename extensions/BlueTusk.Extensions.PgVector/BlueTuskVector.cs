using System.Collections;
using System.Globalization;
using System.Text;

namespace BlueTusk.Extensions.PgVector;

/// <summary>An immutable dense single-precision vector for PostgreSQL pgvector.</summary>
public sealed class BlueTuskVector : IReadOnlyList<float>, IEquatable<BlueTuskVector>
{
    /// <summary>The maximum dimension supported by pgvector's <c>vector</c> type.</summary>
    public const int MaxDimensions = 16_000;

    private readonly float[] _values;

    public BlueTuskVector(params float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Validate(values);
        _values = values.ToArray();
    }

    public int Count => _values.Length;

    public float this[int index] => _values[index];

    /// <summary>Exposes the immutable vector values without allocating a copy.</summary>
    public ReadOnlySpan<float> AsSpan() => _values;

    public static BlueTuskVector Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Parse(value.AsSpan());
    }

    public static BlueTuskVector Parse(ReadOnlySpan<char> value)
    {
        value = value.Trim();
        if (value.Length < 3 || value[0] != '[' || value[^1] != ']')
        {
            throw new FormatException("A pgvector value must be enclosed in '[' and ']'.");
        }

        var contents = value[1..^1];
        var values = new List<float>();
        while (true)
        {
            var separator = contents.IndexOf(',');
            var token = (separator < 0 ? contents : contents[..separator]).Trim();
            if (token.IsEmpty ||
                !float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var element))
            {
                throw new FormatException("The pgvector value contains an invalid element.");
            }

            ValidateElement(element, nameof(value));
            values.Add(element);
            if (values.Count > MaxDimensions)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    $"A pgvector value cannot contain more than {MaxDimensions} dimensions.");
            }

            if (separator < 0)
            {
                break;
            }

            contents = contents[(separator + 1)..];
        }

        return new BlueTuskVector(values.ToArray());
    }

    public bool Equals(BlueTuskVector? other) =>
        other is not null && _values.AsSpan().SequenceEqual(other._values);

    public override bool Equals(object? obj) => obj is BlueTuskVector other && Equals(other);

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

            builder.Append(_values[index].ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.Append(']').ToString();
    }

    public IEnumerator<float> GetEnumerator() => ((IEnumerable<float>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();

    private static void Validate(ReadOnlySpan<float> values)
    {
        if (values.IsEmpty)
        {
            throw new ArgumentException("A pgvector value must contain at least one dimension.", nameof(values));
        }

        if (values.Length > MaxDimensions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                $"A pgvector value cannot contain more than {MaxDimensions} dimensions.");
        }

        foreach (var value in values)
        {
            ValidateElement(value, nameof(values));
        }
    }

    private static void ValidateElement(float value, string parameterName)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "pgvector elements must be finite single-precision values.");
        }
    }
}

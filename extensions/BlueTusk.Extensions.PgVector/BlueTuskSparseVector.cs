using System.Collections;
using System.Globalization;
using System.Text;

namespace BlueTusk.Extensions.PgVector;

/// <summary>One zero-based, non-zero element in a <see cref="BlueTuskSparseVector"/>.</summary>
public readonly record struct BlueTuskSparseVectorElement(int Index, float Value);

/// <summary>An immutable sparse single-precision vector for PostgreSQL pgvector.</summary>
public sealed class BlueTuskSparseVector :
    IReadOnlyList<BlueTuskSparseVectorElement>,
    IEquatable<BlueTuskSparseVector>
{
    public const int MaxDimensions = 1_000_000_000;
    public const int MaxNonZeroElements = 16_000;

    private readonly BlueTuskSparseVectorElement[] _elements;

    public BlueTuskSparseVector(
        int dimensions,
        params BlueTuskSparseVectorElement[] elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ValidateDimensions(dimensions);
        if (elements.Length > MaxNonZeroElements || elements.Length > dimensions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elements),
                $"A sparsevec cannot contain more than {MaxNonZeroElements} non-zero elements or more elements than dimensions.");
        }

        _elements = elements.OrderBy(element => element.Index).ToArray();
        for (var index = 0; index < _elements.Length; index++)
        {
            var element = _elements[index];
            if (element.Index < 0 || element.Index >= dimensions)
            {
                throw new ArgumentOutOfRangeException(nameof(elements), "A sparsevec index is outside its dimensions.");
            }

            if (index > 0 && element.Index == _elements[index - 1].Index)
            {
                throw new ArgumentException("A sparsevec cannot contain duplicate indices.", nameof(elements));
            }

            if (!float.IsFinite(element.Value) || element.Value == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(elements),
                    "Sparsevec elements must be finite and non-zero.");
            }
        }

        Dimensions = dimensions;
    }

    public int Dimensions { get; }

    public int Count => _elements.Length;

    public BlueTuskSparseVectorElement this[int index] => _elements[index];

    public ReadOnlySpan<BlueTuskSparseVectorElement> AsSpan() => _elements;

    public static BlueTuskSparseVector Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        var close = text.IndexOf('}');
        if (text.Length < 4 || text[0] != '{' || close < 1 || close + 1 >= text.Length || text[close + 1] != '/')
        {
            throw new FormatException("A sparsevec value must use the form '{index:value,...}/dimensions'.");
        }

        if (!int.TryParse(text[(close + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var dimensions))
        {
            throw new FormatException("The sparsevec dimension count is invalid.");
        }

        ValidateDimensions(dimensions);
        var contents = text[1..close].Trim();
        var elements = new List<BlueTuskSparseVectorElement>();
        while (!contents.IsEmpty)
        {
            var separator = contents.IndexOf(',');
            var entry = (separator < 0 ? contents : contents[..separator]).Trim();
            var colon = entry.IndexOf(':');
            if (colon <= 0 || colon == entry.Length - 1 || entry[(colon + 1)..].Contains(':'))
            {
                throw new FormatException("The sparsevec value contains an invalid element.");
            }

            if (!int.TryParse(entry[..colon].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var sqlIndex) ||
                !float.TryParse(entry[(colon + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var element))
            {
                throw new FormatException("The sparsevec value contains an invalid element.");
            }

            if (element != 0)
            {
                elements.Add(new BlueTuskSparseVectorElement(checked(sqlIndex - 1), element));
            }

            if (elements.Count > MaxNonZeroElements)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    $"A sparsevec cannot contain more than {MaxNonZeroElements} non-zero elements.");
            }

            if (separator < 0)
            {
                break;
            }

            contents = contents[(separator + 1)..];
        }

        return new BlueTuskSparseVector(dimensions, elements.ToArray());
    }

    public bool Equals(BlueTuskSparseVector? other) =>
        other is not null && Dimensions == other.Dimensions && _elements.AsSpan().SequenceEqual(other._elements);

    public override bool Equals(object? obj) => obj is BlueTuskSparseVector other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Dimensions);
        foreach (var element in _elements)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var builder = new StringBuilder().Append('{');
        for (var index = 0; index < _elements.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(_elements[index].Index + 1)
                .Append(':')
                .Append(_elements[index].Value.ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.Append("}/").Append(Dimensions).ToString();
    }

    public IEnumerator<BlueTuskSparseVectorElement> GetEnumerator() =>
        ((IEnumerable<BlueTuskSparseVectorElement>)_elements).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _elements.GetEnumerator();

    private static void ValidateDimensions(int dimensions)
    {
        if (dimensions is < 1 or > MaxDimensions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dimensions),
                $"A sparsevec must contain between 1 and {MaxDimensions} dimensions.");
        }
    }
}

using System.Collections;

namespace BlueTusk.TypeSystem;

/// <summary>An immutable PostgreSQL <c>int2vector</c> value.</summary>
public sealed class BlueTuskInt16Vector :
    IReadOnlyList<short>,
    IEquatable<BlueTuskInt16Vector>
{
    private readonly short[] _values;

    public BlueTuskInt16Vector(IEnumerable<short> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values.ToArray();
    }

    public int Count => _values.Length;

    public short this[int index] => _values[index];

    public bool Equals(BlueTuskInt16Vector? other) =>
        other is not null && _values.AsSpan().SequenceEqual(other._values);

    public override bool Equals(object? obj) =>
        obj is BlueTuskInt16Vector other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in _values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<short> GetEnumerator() =>
        ((IEnumerable<short>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();
}

/// <summary>An immutable PostgreSQL <c>oidvector</c> value.</summary>
public sealed class BlueTuskObjectIdentifierVector :
    IReadOnlyList<uint>,
    IEquatable<BlueTuskObjectIdentifierVector>
{
    private readonly uint[] _values;

    public BlueTuskObjectIdentifierVector(IEnumerable<uint> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values.ToArray();
    }

    public int Count => _values.Length;

    public uint this[int index] => _values[index];

    public bool Equals(BlueTuskObjectIdentifierVector? other) =>
        other is not null && _values.AsSpan().SequenceEqual(other._values);

    public override bool Equals(object? obj) =>
        obj is BlueTuskObjectIdentifierVector other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in _values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<uint> GetEnumerator() =>
        ((IEnumerable<uint>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();
}

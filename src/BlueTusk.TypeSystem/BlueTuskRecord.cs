using System.Collections;

namespace BlueTusk.TypeSystem;

/// <summary>One field of a named composite or anonymous PostgreSQL record.</summary>
public sealed record BlueTuskRecordField(
    string? Name,
    BlueTuskTypeDescriptor? Type,
    object? Value);

/// <summary>A lossless, ordered PostgreSQL composite or anonymous record value.</summary>
public sealed class BlueTuskRecord : IReadOnlyList<BlueTuskRecordField>
{
    private readonly BlueTuskRecordField[] _fields;

    public BlueTuskRecord(IEnumerable<BlueTuskRecordField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _fields = fields.ToArray();
        if (_fields.Any(field => field is null))
        {
            throw new ArgumentException("Record fields cannot contain null field descriptors.", nameof(fields));
        }
    }

    public int Count => _fields.Length;

    public BlueTuskRecordField this[int index] => _fields[index];

    public BlueTuskRecordField this[string name]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);
            return _fields.Single(field => string.Equals(field.Name, name, StringComparison.Ordinal));
        }
    }

    public IEnumerator<BlueTuskRecordField> GetEnumerator() =>
        ((IEnumerable<BlueTuskRecordField>)_fields).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

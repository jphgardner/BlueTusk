using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Streams;

public sealed record ChangeSourceIdentity
{
    public ChangeSourceIdentity(
        string systemIdentifier,
        string databaseName,
        string slotName,
        string publicationFingerprint)
    {
        SystemIdentifier = RequireValue(systemIdentifier, nameof(systemIdentifier));
        DatabaseName = RequireValue(databaseName, nameof(databaseName));
        SlotName = RequireValue(slotName, nameof(slotName));
        PublicationFingerprint = RequireValue(publicationFingerprint, nameof(publicationFingerprint));
        Fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\n', SystemIdentifier, DatabaseName, SlotName, PublicationFingerprint))));
    }

    public string SystemIdentifier { get; }

    public string DatabaseName { get; }

    public string SlotName { get; }

    public string PublicationFingerprint { get; }

    public string Fingerprint { get; }

    private static string RequireValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

public sealed record ChangeTypeIdentity(uint Oid, string Namespace, string Name);

public sealed record ChangeColumn(
    int Ordinal,
    string Name,
    uint TypeOid,
    int TypeModifier,
    bool IsKey,
    ChangeTypeIdentity? Type = null);

public sealed class ChangeTable
{
    private readonly ReadOnlyCollection<ChangeColumn> _columns;
    private readonly Dictionary<string, int> _columnOrdinals;
    private readonly int[] _keyOrdinals;

    public ChangeTable(
        uint relationId,
        string schema,
        string name,
        char replicaIdentity,
        IEnumerable<ChangeColumn> columns)
        : this(
            relationId,
            schema,
            name,
            replicaIdentity,
            MaterializeColumns(columns))
    {
    }

    private ChangeTable(
        uint relationId,
        string schema,
        string name,
        char replicaIdentity,
        ChangeColumn[] columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        RelationId = relationId;
        Schema = schema;
        Name = name;
        ReplicaIdentity = replicaIdentity;
        _columnOrdinals = new Dictionary<string, int>(columns.Length, StringComparer.Ordinal);
        var keyCount = 0;
        for (var index = 0; index < columns.Length; index++)
        {
            var column = columns[index];
            if (column.Ordinal != index)
            {
                throw new ArgumentException("Column ordinals must be contiguous and zero based.", nameof(columns));
            }

            if (!_columnOrdinals.TryAdd(column.Name, column.Ordinal))
            {
                throw new ArgumentException(
                    $"Column names must be unique; '{column.Name}' is repeated.",
                    nameof(columns));
            }

            if (column.IsKey)
            {
                keyCount++;
            }
        }

        _keyOrdinals = new int[keyCount];
        for (int index = 0, keyIndex = 0; index < columns.Length; index++)
        {
            if (columns[index].IsKey)
            {
                _keyOrdinals[keyIndex++] = index;
            }
        }

        _columns = Array.AsReadOnly(columns);
    }

    public uint RelationId { get; }

    public string Schema { get; }

    public string Name { get; }

    public char ReplicaIdentity { get; }

    public IReadOnlyList<ChangeColumn> Columns => _columns;

    internal ReadOnlySpan<int> KeyOrdinals => _keyOrdinals;

    internal int GetColumnOrdinal(string name) => _columnOrdinals[name];

    internal bool TryGetColumn(string name, [NotNullWhen(true)] out ChangeColumn? column)
    {
        if (_columnOrdinals.TryGetValue(name, out var ordinal))
        {
            column = _columns[ordinal];
            return true;
        }

        column = null;
        return false;
    }

    internal static ChangeTable CreateOwned(
        uint relationId,
        string schema,
        string name,
        char replicaIdentity,
        ChangeColumn[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return new ChangeTable(relationId, schema, name, replicaIdentity, columns);
    }

    public override string ToString() => $"{Schema}.{Name}";

    private static ChangeColumn[] MaterializeColumns(IEnumerable<ChangeColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return columns.ToArray();
    }
}

public enum ChangeColumnState
{
    Value,
    DatabaseNull,
    NotPublished,
    OldValueUnavailable,
    UnchangedToast,
    DecodingFailure,
}

public enum ChangeValueEncoding
{
    None,
    Text,
    Binary,
}

public sealed class ChangeColumnValue : IEquatable<ChangeColumnValue>
{
    private readonly ReadOnlyMemory<byte> _data;

    private ChangeColumnValue(
        ChangeColumnState state,
        ChangeValueEncoding encoding,
        ReadOnlySpan<byte> data,
        string? decodingError,
        ReadOnlyMemory<byte>? ownedData = null)
    {
        State = state;
        Encoding = encoding;
        _data = ownedData ?? data.ToArray();
        DecodingError = decodingError;
    }

    public ChangeColumnState State { get; }

    public ChangeValueEncoding Encoding { get; }

    public ReadOnlyMemory<byte> Data => _data;

    public string? DecodingError { get; }

    public static ChangeColumnValue FromValue(ReadOnlySpan<byte> data, ChangeValueEncoding encoding)
    {
        if (encoding == ChangeValueEncoding.None)
        {
            throw new ArgumentOutOfRangeException(nameof(encoding));
        }

        return new ChangeColumnValue(ChangeColumnState.Value, encoding, data, null);
    }

    internal static ChangeColumnValue FromOwnedValue(ReadOnlyMemory<byte> data, ChangeValueEncoding encoding) =>
        new(ChangeColumnState.Value, encoding, data.Span, null, data);

    public static ChangeColumnValue DatabaseNull { get; } =
        new(ChangeColumnState.DatabaseNull, ChangeValueEncoding.None, [], null);

    public static ChangeColumnValue NotPublished { get; } =
        new(ChangeColumnState.NotPublished, ChangeValueEncoding.None, [], null);

    public static ChangeColumnValue OldValueUnavailable { get; } =
        new(ChangeColumnState.OldValueUnavailable, ChangeValueEncoding.None, [], null);

    public static ChangeColumnValue UnchangedToast { get; } =
        new(ChangeColumnState.UnchangedToast, ChangeValueEncoding.None, [], null);

    public static ChangeColumnValue DecodingFailure(ReadOnlySpan<byte> data, ChangeValueEncoding encoding, string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new ChangeColumnValue(ChangeColumnState.DecodingFailure, encoding, data, error);
    }

    public bool Equals(ChangeColumnValue? other) =>
        other is not null &&
        State == other.State &&
        Encoding == other.Encoding &&
        string.Equals(DecodingError, other.DecodingError, StringComparison.Ordinal) &&
        _data.Span.SequenceEqual(other._data.Span);

    public override bool Equals(object? obj) => Equals(obj as ChangeColumnValue);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(State);
        hash.Add(Encoding);
        hash.Add(DecodingError, StringComparer.Ordinal);
        foreach (var item in _data.Span)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

public sealed class ChangeRow : IReadOnlyList<ChangeColumnValue>
{
    private readonly ChangeColumnValue[] _values;

    public ChangeRow(ChangeTable table, IEnumerable<ChangeColumnValue> values)
        : this(table, MaterializeValues(values))
    {
    }

    private ChangeRow(ChangeTable table, ChangeColumnValue[] values)
    {
        ArgumentNullException.ThrowIfNull(table);
        Table = table;
        if (values.Length != table.Columns.Count)
        {
            throw new ArgumentException("A change row must contain one state for every table column.", nameof(values));
        }

        _values = values;
    }

    public ChangeTable Table { get; }

    public IReadOnlyList<ChangeColumnValue> Values => this;

    public ChangeColumnValue this[int ordinal] => _values[ordinal];

    public ChangeColumnValue this[string name] => _values[Table.GetColumnOrdinal(name)];

    int IReadOnlyCollection<ChangeColumnValue>.Count => _values.Length;

    IEnumerator<ChangeColumnValue> IEnumerable<ChangeColumnValue>.GetEnumerator() =>
        ((IEnumerable<ChangeColumnValue>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();

    internal static ChangeRow CreateOwned(ChangeTable table, ChangeColumnValue[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ChangeRow(table, values);
    }

    private static ChangeColumnValue[] MaterializeValues(IEnumerable<ChangeColumnValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.ToArray();
    }
}

public sealed class ChangeRow<T>
{
    public ChangeRow(ChangeRow columns, T? value, bool hasValue)
    {
        ArgumentNullException.ThrowIfNull(columns);
        Columns = columns;
        Value = value;
        HasValue = hasValue;
    }

    public ChangeRow Columns { get; }

    public T? Value { get; }

    public bool HasValue { get; }
}

public sealed class ChangedColumnSet
{
    private readonly ReadOnlyCollection<int> _ordinals;

    public ChangedColumnSet(bool isExact, IEnumerable<int> ordinals)
    {
        ArgumentNullException.ThrowIfNull(ordinals);
        IsExact = isExact;
        _ordinals = Array.AsReadOnly(ordinals.Distinct().Order().ToArray());
    }

    public bool IsExact { get; }

    public IReadOnlyList<int> Ordinals => _ordinals;
}

using System.Collections.ObjectModel;
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

    public ChangeTable(
        uint relationId,
        string schema,
        string name,
        char replicaIdentity,
        IEnumerable<ChangeColumn> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);

        RelationId = relationId;
        Schema = schema;
        Name = name;
        ReplicaIdentity = replicaIdentity;
        var materialized = columns.ToArray();
        for (var index = 0; index < materialized.Length; index++)
        {
            if (materialized[index].Ordinal != index)
            {
                throw new ArgumentException("Column ordinals must be contiguous and zero based.", nameof(columns));
            }
        }

        _columns = Array.AsReadOnly(materialized);
    }

    public uint RelationId { get; }

    public string Schema { get; }

    public string Name { get; }

    public char ReplicaIdentity { get; }

    public IReadOnlyList<ChangeColumn> Columns => _columns;

    public override string ToString() => $"{Schema}.{Name}";
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

public sealed class ChangeRow
{
    private readonly ReadOnlyCollection<ChangeColumnValue> _values;
    private readonly Dictionary<string, int> _ordinals;

    public ChangeRow(ChangeTable table, IEnumerable<ChangeColumnValue> values)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(values);
        Table = table;
        var materialized = values.ToArray();
        if (materialized.Length != table.Columns.Count)
        {
            throw new ArgumentException("A change row must contain one state for every table column.", nameof(values));
        }

        _values = Array.AsReadOnly(materialized);
        _ordinals = table.Columns.ToDictionary(column => column.Name, column => column.Ordinal, StringComparer.Ordinal);
    }

    public ChangeTable Table { get; }

    public IReadOnlyList<ChangeColumnValue> Values => _values;

    public ChangeColumnValue this[int ordinal] => _values[ordinal];

    public ChangeColumnValue this[string name] => _values[_ordinals[name]];
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

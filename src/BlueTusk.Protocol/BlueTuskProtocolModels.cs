using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;

namespace BlueTusk.Protocol;

public sealed record BlueTuskParameterStatus(string Name, string Value);

public readonly record struct BlueTuskBackendKeyData(int ProcessId, int SecretKey);

public sealed record BlueTuskNotificationResponse(
    int ProcessId,
    string Channel,
    string Payload);

public enum BlueTuskTransactionStatus : byte
{
    Idle = (byte)'I',
    InTransaction = (byte)'T',
    FailedTransaction = (byte)'E',
}

public sealed record BlueTuskFieldDescription(
    string Name,
    uint TableOid,
    short ColumnAttributeNumber,
    uint TypeOid,
    short TypeSize,
    int TypeModifier,
    short FormatCode);

public sealed record BlueTuskDataRow(IReadOnlyList<ReadOnlyMemory<byte>?> Values);

internal sealed class BlueTuskDataRowValues : IReadOnlyList<ReadOnlyMemory<byte>?>
{
    private readonly byte[] _payload;
    private readonly int _rowOffset;

    internal BlueTuskDataRowValues(byte[] payload, int count)
        : this(payload, 0, count)
    {
    }

    internal BlueTuskDataRowValues(byte[] payload, int rowOffset, int count)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfNegative(rowOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowOffset, payload.Length);

        _payload = payload;
        _rowOffset = rowOffset;
        Count = count;
    }

    public int Count { get; }

    public ReadOnlyMemory<byte>? this[int index]
    {
        get => GetValue(_payload, _rowOffset, Count, index);
    }

    internal static ReadOnlyMemory<byte>? GetValue(byte[] rowPayload, int fieldCount, int index)
        => GetValue(rowPayload, 0, fieldCount, index);

    internal static ReadOnlyMemory<byte>? GetValue(
        byte[] rowPayload,
        int rowOffset,
        int fieldCount,
        int index)
    {
        var nextField = 0;
        var offset = rowOffset + sizeof(short);
        return GetValue(rowPayload, fieldCount, index, ref nextField, ref offset);
    }

    internal static ReadOnlyMemory<byte>? GetValue(
        byte[] rowPayload,
        int fieldCount,
        int index,
        ref int nextField,
        ref int offset) =>
        GetValue(rowPayload, 0, fieldCount, index, ref nextField, ref offset);

    internal static ReadOnlyMemory<byte>? GetValue(
        byte[] rowPayload,
        int rowOffset,
        int fieldCount,
        int index,
        ref int nextField,
        ref int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, fieldCount);
        if (index < nextField || nextField > fieldCount)
        {
            nextField = 0;
            offset = rowOffset + sizeof(short);
        }

        while (nextField <= index)
        {
            var fieldIndex = nextField++;
            var length = BinaryPrimitives.ReadInt32BigEndian(rowPayload.AsSpan(offset));
            offset += sizeof(int);
            var valueOffset = offset;
            if (length >= 0)
            {
                offset += length;
            }

            if (fieldIndex == index)
            {
                return length == -1
                    ? (ReadOnlyMemory<byte>?)null
                    : rowPayload.AsMemory(valueOffset, length);
            }
        }

        throw new UnreachableException();
    }

    public IEnumerator<ReadOnlyMemory<byte>?> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public enum BlueTuskCopyFormat : byte
{
    Text = 0,
    Binary = 1,
}

public sealed record BlueTuskCopyResponse(
    BlueTuskCopyFormat Format,
    IReadOnlyList<BlueTuskCopyFormat> ColumnFormats);

public sealed record BlueTuskError(IReadOnlyDictionary<char, string> Fields)
{
    public string Severity => Get('V') ?? Get('S') ?? "ERROR";

    public string? SqlState => Get('C');

    public string Message => Get('M') ?? "PostgreSQL reported an unspecified error.";

    public string? Detail => Get('D');

    public string? Hint => Get('H');

    private string? Get(char code) => Fields.TryGetValue(code, out var value) ? value : null;
}

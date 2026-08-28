using System.Buffers;
using System.Collections;
using BlueTusk.Protocol;

namespace BlueTusk.Client;

internal readonly record struct BlueTuskBufferedRow(
    byte[] Payload,
    int Offset,
    int FieldCount)
{
    internal ReadOnlyMemory<byte>? GetValue(int fieldIndex) =>
        BlueTuskDataRowValues.GetValue(Payload, Offset, FieldCount, fieldIndex);

    internal ReadOnlyMemory<byte>? GetValue(
        int fieldIndex,
        ref int nextField,
        ref int offset) =>
        BlueTuskDataRowValues.GetValue(
            Payload,
            Offset,
            FieldCount,
            fieldIndex,
            ref nextField,
            ref offset);
}

internal sealed class BlueTuskBufferedRows : IReadOnlyList<BlueTuskDataRow>
{
    private const int InitialPooledPayloadSize = 32;
    [ThreadStatic]
    private static Stack<BlueTuskBufferedRows>? t_pool;
    private int[]? _offsetBuilder;
    private int[]? _additionalOffsets;
    private int _additionalCount;
    private byte[]? _payloadBuilder;
    private byte[]? _payload;
    private int _payloadCount;
    private BlueTuskDataRow?[]? _materialized;
    private bool _completed;
    private int _fieldCount;
    private bool _rented;

    public int Count { get; private set; }

    internal static BlueTuskBufferedRows Rent()
    {
        var pool = t_pool;
        var rows = pool is { Count: > 0 } ? pool.Pop() : new BlueTuskBufferedRows();
        rows._rented = true;
        return rows;
    }

    public BlueTuskDataRow this[int index]
    {
        get
        {
            var row = GetRow(index);
            var materialized = _materialized ??= new BlueTuskDataRow?[Count];
            return materialized[index] ??= new BlueTuskDataRow(
                new BlueTuskDataRowValues(
                    row.Payload,
                    row.Offset,
                    row.FieldCount));
        }
    }

    internal void Add(BlueTuskBackendMessage message, int expectedFieldCount)
    {
        if (_completed)
        {
            throw new InvalidOperationException("Buffered rows have already been completed.");
        }

        var fieldCount = BlueTuskBackendMessageDecoder.ValidateDataRowPayload(
            message,
            expectedFieldCount);
        var length = checked((int)message.Length);
        EnsurePayloadCapacity(checked(_payloadCount + length));
        message.Payload.CopyTo(_payloadBuilder!.AsSpan(_payloadCount, length));
        var rowOffset = _payloadCount;
        _payloadCount += length;
        if (Count == 0)
        {
            _fieldCount = fieldCount;
        }
        else
        {
            AddAdditional(rowOffset);
        }

        Count++;
    }

    internal void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        if (_rented)
        {
            _payload = _payloadCount == 0 ? [] : _payloadBuilder;
            _payloadBuilder = null;
        }
        else
        {
            _payload = _payloadCount == 0 ? [] : new byte[_payloadCount];
            if (_payloadCount != 0)
            {
                _payloadBuilder!.AsSpan(0, _payloadCount).CopyTo(_payload);
            }

            ReturnPayloadBuilder();
        }

        if (_additionalCount != 0)
        {
            if (_rented)
            {
                _additionalOffsets = _offsetBuilder;
                _offsetBuilder = null;
            }
            else
            {
                _additionalOffsets = new int[_additionalCount];
                _offsetBuilder!.AsSpan(0, _additionalCount).CopyTo(_additionalOffsets);
            }
        }

        ReturnMetadataBuilder();
    }

    internal void Release()
    {
        if (!_rented)
        {
            return;
        }

        _rented = false;
        if (_payload is { Length: > 0 })
        {
            ArrayPool<byte>.Shared.Return(_payload, clearArray: true);
        }

        if (_additionalOffsets is not null)
        {
            ArrayPool<int>.Shared.Return(_additionalOffsets);
        }

        ReturnPayloadBuilder();
        ReturnMetadataBuilder();
        _payload = null;
        _additionalOffsets = null;
        _materialized = null;
        _payloadCount = 0;
        _additionalCount = 0;
        _fieldCount = 0;
        Count = 0;
        _completed = false;
        var pool = t_pool ??= new Stack<BlueTuskBufferedRows>(16);
        if (pool.Count < 256)
        {
            pool.Push(this);
        }
    }

    internal ReadOnlyMemory<byte>? GetValue(int rowIndex, int fieldIndex)
    {
        return GetRow(rowIndex).GetValue(fieldIndex);
    }

    internal ReadOnlyMemory<byte>? GetValue(
        int rowIndex,
        int fieldIndex,
        ref int nextField,
        ref int offset)
    {
        return GetRow(rowIndex).GetValue(fieldIndex, ref nextField, ref offset);
    }

    public IEnumerator<BlueTuskDataRow> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal BlueTuskBufferedRow GetRow(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        Complete();
        return CreateRow(
            _payload!,
            index == 0 ? 0 : _additionalOffsets![index - 1],
            _fieldCount);
    }

    private void AddAdditional(int rowOffset)
    {
        if (_offsetBuilder is null)
        {
            _offsetBuilder = ArrayPool<int>.Shared.Rent(16);
        }
        else if (_additionalCount == _offsetBuilder.Length)
        {
            var replacement = ArrayPool<int>.Shared.Rent(
                checked(_offsetBuilder.Length * 2));
            _offsetBuilder.AsSpan(0, _additionalCount).CopyTo(replacement);
            ArrayPool<int>.Shared.Return(_offsetBuilder);
            _offsetBuilder = replacement;
        }

        _offsetBuilder[_additionalCount++] = rowOffset;
    }

    private void EnsurePayloadCapacity(int required)
    {
        if (_payloadBuilder is not null && required <= _payloadBuilder.Length)
        {
            return;
        }

        var replacement = ArrayPool<byte>.Shared.Rent(
            Math.Max(
                required,
                _payloadBuilder is null
                    ? (_rented ? InitialPooledPayloadSize : 4096)
                    : checked(_payloadBuilder.Length * 2)));
        if (_payloadCount != 0)
        {
            _payloadBuilder!.AsSpan(0, _payloadCount).CopyTo(replacement);
        }

        ReturnPayloadBuilder();
        _payloadBuilder = replacement;
    }

    private void ReturnPayloadBuilder()
    {
        if (_payloadBuilder is not null)
        {
            ArrayPool<byte>.Shared.Return(_payloadBuilder, clearArray: true);
            _payloadBuilder = null;
        }
    }

    private void ReturnMetadataBuilder()
    {
        if (_offsetBuilder is not null)
        {
            ArrayPool<int>.Shared.Return(_offsetBuilder);
            _offsetBuilder = null;
        }
    }

    private static BlueTuskBufferedRow CreateRow(byte[] payload, int offset, int fieldCount) =>
        new(payload, offset, fieldCount);
}

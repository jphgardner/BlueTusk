using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.TypeSystem;

/// <summary>A bounds-checked reader over one PostgreSQL field value.</summary>
public ref struct BlueTuskReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _offset;

    public BlueTuskReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _offset = 0;
    }

    public readonly int Remaining => _buffer.Length - _offset;

    public int ReadInt32BigEndian()
    {
        EnsureRemaining(sizeof(int));
        var value = BinaryPrimitives.ReadInt32BigEndian(_buffer[_offset..]);
        _offset += sizeof(int);
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        EnsureRemaining(count);
        var value = _buffer.Slice(_offset, count);
        _offset += count;
        return value;
    }

    public string ReadRemainingUtf8()
    {
        var value = Encoding.UTF8.GetString(_buffer[_offset..]);
        _offset = _buffer.Length;
        return value;
    }

    private readonly void EnsureRemaining(int count)
    {
        if (Remaining < count)
        {
            throw new InvalidOperationException($"The field contains {Remaining} unread bytes; {count} were requested.");
        }
    }
}

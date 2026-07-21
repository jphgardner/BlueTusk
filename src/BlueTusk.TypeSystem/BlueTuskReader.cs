using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.TypeSystem;

/// <summary>A bounds-checked reader over one PostgreSQL field value.</summary>
public ref struct BlueTuskReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlySpan<byte> _buffer;
    private int _offset;

    public BlueTuskReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _offset = 0;
    }

    public readonly int Remaining => _buffer.Length - _offset;

    public byte ReadByte()
    {
        EnsureRemaining(sizeof(byte));
        return _buffer[_offset++];
    }

    public short ReadInt16BigEndian()
    {
        EnsureRemaining(sizeof(short));
        var value = BinaryPrimitives.ReadInt16BigEndian(_buffer[_offset..]);
        _offset += sizeof(short);
        return value;
    }

    public ushort ReadUInt16BigEndian()
    {
        EnsureRemaining(sizeof(ushort));
        var value = BinaryPrimitives.ReadUInt16BigEndian(_buffer[_offset..]);
        _offset += sizeof(ushort);
        return value;
    }

    public int ReadInt32BigEndian()
    {
        EnsureRemaining(sizeof(int));
        var value = BinaryPrimitives.ReadInt32BigEndian(_buffer[_offset..]);
        _offset += sizeof(int);
        return value;
    }

    public uint ReadUInt32BigEndian()
    {
        EnsureRemaining(sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32BigEndian(_buffer[_offset..]);
        _offset += sizeof(uint);
        return value;
    }

    public long ReadInt64BigEndian()
    {
        EnsureRemaining(sizeof(long));
        var value = BinaryPrimitives.ReadInt64BigEndian(_buffer[_offset..]);
        _offset += sizeof(long);
        return value;
    }

    public ulong ReadUInt64BigEndian()
    {
        EnsureRemaining(sizeof(ulong));
        var value = BinaryPrimitives.ReadUInt64BigEndian(_buffer[_offset..]);
        _offset += sizeof(ulong);
        return value;
    }

    public float ReadSingleBigEndian() => BitConverter.Int32BitsToSingle(ReadInt32BigEndian());

    public double ReadDoubleBigEndian() => BitConverter.Int64BitsToDouble(ReadInt64BigEndian());

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        EnsureRemaining(count);
        var value = _buffer.Slice(_offset, count);
        _offset += count;
        return value;
    }

    public ReadOnlySpan<byte> ReadRemainingBytes() => ReadBytes(Remaining);

    public string ReadRemainingUtf8()
    {
        var value = StrictUtf8.GetString(_buffer[_offset..]);
        _offset = _buffer.Length;
        return value;
    }

    public string ReadNullTerminatedUtf8()
    {
        var remaining = _buffer[_offset..];
        var terminator = remaining.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidOperationException("The field does not contain a null-terminated UTF-8 value.");
        }

        var value = StrictUtf8.GetString(remaining[..terminator]);
        _offset += terminator + 1;
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

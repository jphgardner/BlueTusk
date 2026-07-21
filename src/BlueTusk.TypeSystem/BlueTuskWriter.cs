using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.TypeSystem;

/// <summary>A bounds-checked writer over caller-owned field storage.</summary>
public ref struct BlueTuskWriter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Span<byte> _buffer;
    private int _offset;

    public BlueTuskWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _offset = 0;
    }

    public readonly int WrittenCount => _offset;

    public void WriteByte(byte value)
    {
        EnsureRemaining(sizeof(byte));
        _buffer[_offset++] = value;
    }

    public void WriteInt16BigEndian(short value)
    {
        EnsureRemaining(sizeof(short));
        BinaryPrimitives.WriteInt16BigEndian(_buffer[_offset..], value);
        _offset += sizeof(short);
    }

    public void WriteUInt16BigEndian(ushort value)
    {
        EnsureRemaining(sizeof(ushort));
        BinaryPrimitives.WriteUInt16BigEndian(_buffer[_offset..], value);
        _offset += sizeof(ushort);
    }

    public void WriteInt32BigEndian(int value)
    {
        EnsureRemaining(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(_buffer[_offset..], value);
        _offset += sizeof(int);
    }

    public void WriteUInt32BigEndian(uint value)
    {
        EnsureRemaining(sizeof(uint));
        BinaryPrimitives.WriteUInt32BigEndian(_buffer[_offset..], value);
        _offset += sizeof(uint);
    }

    public void WriteInt64BigEndian(long value)
    {
        EnsureRemaining(sizeof(long));
        BinaryPrimitives.WriteInt64BigEndian(_buffer[_offset..], value);
        _offset += sizeof(long);
    }

    public void WriteUInt64BigEndian(ulong value)
    {
        EnsureRemaining(sizeof(ulong));
        BinaryPrimitives.WriteUInt64BigEndian(_buffer[_offset..], value);
        _offset += sizeof(ulong);
    }

    public void WriteSingleBigEndian(float value) => WriteInt32BigEndian(BitConverter.SingleToInt32Bits(value));

    public void WriteDoubleBigEndian(double value) => WriteInt64BigEndian(BitConverter.DoubleToInt64Bits(value));

    public void WriteBytes(scoped ReadOnlySpan<byte> value)
    {
        EnsureRemaining(value.Length);
        value.CopyTo(_buffer[_offset..]);
        _offset += value.Length;
    }

    public void WriteUtf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = StrictUtf8.GetByteCount(value);
        EnsureRemaining(count);
        _offset += StrictUtf8.GetBytes(value, _buffer[_offset..]);
    }

    private readonly void EnsureRemaining(int count)
    {
        if (_buffer.Length - _offset < count)
        {
            throw new InvalidOperationException(
                $"The destination contains {_buffer.Length - _offset} unwritten bytes; {count} are required.");
        }
    }
}

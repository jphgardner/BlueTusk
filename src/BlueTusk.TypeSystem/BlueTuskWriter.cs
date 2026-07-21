using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.TypeSystem;

/// <summary>A bounds-checked writer over caller-owned field storage.</summary>
public ref struct BlueTuskWriter
{
    private readonly Span<byte> _buffer;
    private int _offset;

    public BlueTuskWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _offset = 0;
    }

    public readonly int WrittenCount => _offset;

    public void WriteInt32BigEndian(int value)
    {
        EnsureRemaining(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(_buffer[_offset..], value);
        _offset += sizeof(int);
    }

    public void WriteUtf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = Encoding.UTF8.GetByteCount(value);
        EnsureRemaining(count);
        _offset += Encoding.UTF8.GetBytes(value, _buffer[_offset..]);
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


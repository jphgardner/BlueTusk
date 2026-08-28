using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.Protocol;

internal ref struct BlueTuskBackendPayloadReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlySpan<byte> _payload;
    private int _offset;

    public BlueTuskBackendPayloadReader(ReadOnlySpan<byte> payload)
    {
        _payload = payload;
        _offset = 0;
    }

    public readonly int Remaining => _payload.Length - _offset;

    public readonly int Offset => _offset;

    public byte ReadByte()
    {
        EnsureRemaining(1);
        return _payload[_offset++];
    }

    public short ReadInt16()
    {
        EnsureRemaining(sizeof(short));
        var value = BinaryPrimitives.ReadInt16BigEndian(_payload[_offset..]);
        _offset += sizeof(short);
        return value;
    }

    public int ReadInt32()
    {
        EnsureRemaining(sizeof(int));
        var value = BinaryPrimitives.ReadInt32BigEndian(_payload[_offset..]);
        _offset += sizeof(int);
        return value;
    }

    public uint ReadUInt32() => unchecked((uint)ReadInt32());

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        EnsureRemaining(count);
        var value = _payload.Slice(_offset, count);
        _offset += count;
        return value;
    }

    public ReadOnlySpan<byte> ReadRemainingBytes()
    {
        var value = _payload[_offset..];
        _offset = _payload.Length;
        return value;
    }

    public string ReadCString()
    {
        var remainder = _payload[_offset..];
        var terminator = remainder.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new BlueTuskProtocolException("A protocol string was not null-terminated.");
        }

        string value;
        try
        {
            value = StrictUtf8.GetString(remainder[..terminator]);
        }
        catch (DecoderFallbackException exception)
        {
            throw new BlueTuskProtocolException("A protocol string was not valid UTF-8.", exception);
        }

        _offset += terminator + 1;
        return value;
    }

    public void EnsureConsumed()
    {
        if (Remaining != 0)
        {
            throw new BlueTuskProtocolException($"A backend message contains {Remaining} unexpected trailing bytes.");
        }
    }

    private readonly void EnsureRemaining(int count)
    {
        if (Remaining < count)
        {
            throw new BlueTuskProtocolException(
                $"A backend message ended with {Remaining} bytes available while {count} were required.");
        }
    }
}

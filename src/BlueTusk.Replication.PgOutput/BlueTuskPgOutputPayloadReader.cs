using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.Replication.PgOutput;

internal ref struct BlueTuskPgOutputPayloadReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlyMemory<byte> _payload;
    private int _offset;

    public BlueTuskPgOutputPayloadReader(ReadOnlyMemory<byte> payload)
    {
        _payload = payload;
    }

    public readonly int Remaining => _payload.Length - _offset;

    public byte ReadByte()
    {
        EnsureRemaining(1);
        return _payload.Span[_offset++];
    }

    public short ReadInt16()
    {
        EnsureRemaining(sizeof(short));
        var value = BinaryPrimitives.ReadInt16BigEndian(_payload.Span[_offset..]);
        _offset += sizeof(short);
        return value;
    }

    public int ReadInt32()
    {
        EnsureRemaining(sizeof(int));
        var value = BinaryPrimitives.ReadInt32BigEndian(_payload.Span[_offset..]);
        _offset += sizeof(int);
        return value;
    }

    public uint ReadUInt32() => unchecked((uint)ReadInt32());

    public long ReadInt64()
    {
        EnsureRemaining(sizeof(long));
        var value = BinaryPrimitives.ReadInt64BigEndian(_payload.Span[_offset..]);
        _offset += sizeof(long);
        return value;
    }

    public ulong ReadUInt64() => unchecked((ulong)ReadInt64());

    public ReadOnlyMemory<byte> ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        EnsureRemaining(count);
        var value = _payload.Slice(_offset, count);
        _offset += count;
        return value;
    }

    public string ReadCString()
    {
        var remainder = _payload.Span[_offset..];
        var terminator = remainder.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A pgoutput string was not null-terminated.");
        }

        try
        {
            var result = StrictUtf8.GetString(remainder[..terminator]);
            _offset += terminator + 1;
            return result;
        }
        catch (DecoderFallbackException exception)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A pgoutput string was not valid UTF-8.",
                exception);
        }
    }

    public void EnsureConsumed()
    {
        if (Remaining != 0)
        {
            throw new BlueTuskPgOutputProtocolException(
                $"A pgoutput message contains {Remaining} unexpected trailing bytes.");
        }
    }

    private readonly void EnsureRemaining(int count)
    {
        if (Remaining < count)
        {
            throw new BlueTuskPgOutputProtocolException(
                $"A pgoutput message ended with {Remaining} bytes available while {count} were required.");
        }
    }
}

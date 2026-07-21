using System.Buffers;

namespace BlueTusk.Protocol;

/// <summary>Parses length-prefixed PostgreSQL backend frames across arbitrary buffer segments.</summary>
public sealed class BlueTuskBackendMessageParser
{
    public const int DefaultMaximumMessageSize = 64 * 1024 * 1024;
    private const int HeaderSize = 5;
    private const int LengthFieldSize = 4;

    public BlueTuskBackendMessageParser(int maximumMessageSize = DefaultMaximumMessageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumMessageSize, LengthFieldSize);

        MaximumMessageSize = maximumMessageSize;
    }

    public int MaximumMessageSize { get; }

    public bool TryParse(ref ReadOnlySequence<byte> buffer, out BlueTuskBackendMessage message)
    {
        message = default;
        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryRead(out var code) || !reader.TryReadBigEndian(out int length))
        {
            return false;
        }

        if (length < LengthFieldSize)
        {
            throw new BlueTuskProtocolException($"Backend message '{(char)code}' declared invalid length {length}.");
        }

        if (length > MaximumMessageSize)
        {
            throw new BlueTuskProtocolException(
                $"Backend message '{(char)code}' declared length {length}, exceeding the configured maximum {MaximumMessageSize}.");
        }

        var payloadLength = length - LengthFieldSize;
        var frameLength = 1L + length;
        if (buffer.Length < frameLength)
        {
            return false;
        }

        var payload = buffer.Slice(reader.Position, payloadLength);
        message = new BlueTuskBackendMessage(code, payload);
        buffer = buffer.Slice(frameLength);
        return true;
    }
}

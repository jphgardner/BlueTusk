using System.Buffers.Binary;

namespace BlueTusk.Protocol.Capture;

/// <summary>Reads the versioned BlueTusk protocol capture format with bounded payload allocation.</summary>
public sealed class BlueTuskProtocolCaptureReader
{
    public const int DefaultMaximumPayloadLength = 64 * 1024 * 1024;

    private readonly Stream _input;
    private readonly int _maximumPayloadLength;

    public BlueTuskProtocolCaptureReader(Stream input, int maximumPayloadLength = DefaultMaximumPayloadLength)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The capture stream must be readable.", nameof(input));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadLength);
        _input = input;
        _maximumPayloadLength = maximumPayloadLength;
        CreatedAt = ReadFileHeader();
    }

    public DateTimeOffset CreatedAt { get; }

    public BlueTuskProtocolCaptureRecord? ReadRecord()
    {
        Span<byte> header = stackalloc byte[BlueTuskProtocolCaptureWriter.RecordHeaderLength];
        var first = _input.ReadByte();
        if (first < 0)
        {
            return null;
        }

        header[0] = checked((byte)first);
        _input.ReadExactly(header[1..]);
        return ReadRecord(header);
    }

    public async ValueTask<BlueTuskProtocolCaptureRecord?> ReadRecordAsync(
        CancellationToken cancellationToken = default)
    {
        var header = new byte[BlueTuskProtocolCaptureWriter.RecordHeaderLength];
        var firstRead = await _input.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (firstRead == 0)
        {
            return null;
        }

        await _input.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        return await ReadRecordAsync(header, cancellationToken).ConfigureAwait(false);
    }

    private DateTimeOffset ReadFileHeader()
    {
        Span<byte> header = stackalloc byte[BlueTuskProtocolCaptureWriter.FileHeaderLength];
        _input.ReadExactly(header);
        if (!header[..8].SequenceEqual(BlueTuskProtocolCaptureWriter.Magic))
        {
            throw new InvalidDataException("The stream is not a BlueTusk protocol capture.");
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(header[8..]);
        if (version != BlueTuskProtocolCaptureWriter.FormatVersion)
        {
            throw new InvalidDataException($"BlueTusk protocol capture version {version} is not supported.");
        }

        ValidateHeaderLength(
            BinaryPrimitives.ReadUInt16BigEndian(header[10..]),
            BlueTuskProtocolCaptureWriter.FileHeaderLength,
            "file");
        if (BinaryPrimitives.ReadUInt32BigEndian(header[12..]) != 0)
        {
            throw new InvalidDataException("The capture uses unsupported file flags.");
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(header[16..]));
    }

    private BlueTuskProtocolCaptureRecord ReadRecord(ReadOnlySpan<byte> header)
    {
        var metadata = ParseRecordHeader(header);
        var payload = new byte[metadata.PayloadLength];
        _input.ReadExactly(payload);
        return new BlueTuskProtocolCaptureRecord(metadata.Direction, metadata.Attributes, metadata.Elapsed, payload);
    }

    private async ValueTask<BlueTuskProtocolCaptureRecord> ReadRecordAsync(
        ReadOnlyMemory<byte> header,
        CancellationToken cancellationToken)
    {
        var metadata = ParseRecordHeader(header.Span);
        var payload = new byte[metadata.PayloadLength];
        await _input.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new BlueTuskProtocolCaptureRecord(metadata.Direction, metadata.Attributes, metadata.Elapsed, payload);
    }

    private RecordMetadata ParseRecordHeader(ReadOnlySpan<byte> header)
    {
        var direction = (BlueTuskCaptureDirection)header[0];
        if (!Enum.IsDefined(direction))
        {
            throw new InvalidDataException($"Capture direction {header[0]} is invalid.");
        }

        var attributes = (BlueTuskCaptureRecordAttributes)header[1];
        if ((attributes & ~(BlueTuskCaptureRecordAttributes.Redacted | BlueTuskCaptureRecordAttributes.Encrypted)) != 0)
        {
            throw new InvalidDataException($"Capture record flags {header[1]} are not supported.");
        }

        ValidateHeaderLength(
            BinaryPrimitives.ReadUInt16BigEndian(header[2..]),
            BlueTuskProtocolCaptureWriter.RecordHeaderLength,
            "record");
        var elapsedMicroseconds = BinaryPrimitives.ReadInt64BigEndian(header[4..]);
        if (elapsedMicroseconds < 0 || elapsedMicroseconds > long.MaxValue / 10)
        {
            throw new InvalidDataException("Capture record elapsed time is outside the supported range.");
        }

        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(header[12..]);
        if (payloadLength < 0 || payloadLength > _maximumPayloadLength)
        {
            throw new InvalidDataException(
                $"Capture payload length {payloadLength} is outside the configured limit {_maximumPayloadLength}.");
        }

        return new RecordMetadata(direction, attributes, TimeSpan.FromTicks(elapsedMicroseconds * 10), payloadLength);
    }

    private static void ValidateHeaderLength(ushort actual, int expected, string kind)
    {
        if (actual != expected)
        {
            throw new InvalidDataException($"The capture {kind} header length {actual} is not supported.");
        }
    }

    private readonly record struct RecordMetadata(
        BlueTuskCaptureDirection Direction,
        BlueTuskCaptureRecordAttributes Attributes,
        TimeSpan Elapsed,
        int PayloadLength);
}

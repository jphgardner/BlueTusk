using System.Buffers.Binary;

namespace BlueTusk.Protocol.Capture;

/// <summary>Writes the versioned BlueTusk protocol capture format.</summary>
public sealed class BlueTuskProtocolCaptureWriter
{
    internal const int FileHeaderLength = 24;
    internal const int RecordHeaderLength = 16;
    internal const ushort FormatVersion = 1;
    internal static ReadOnlySpan<byte> Magic => "BTPCAP\r\n"u8;

    private readonly Stream _output;

    public BlueTuskProtocolCaptureWriter(Stream output, DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The capture stream must be writable.", nameof(output));
        }

        _output = output;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        WriteFileHeader();
    }

    public DateTimeOffset CreatedAt { get; }

    public void WriteRecord(BlueTuskProtocolCaptureRecord record)
    {
        ValidateRecord(record);
        Span<byte> header = stackalloc byte[RecordHeaderLength];
        WriteRecordHeader(header, record);
        _output.Write(header);
        _output.Write(record.Payload.Span);
    }

    public async ValueTask WriteRecordAsync(
        BlueTuskProtocolCaptureRecord record,
        CancellationToken cancellationToken = default)
    {
        ValidateRecord(record);
        var header = new byte[RecordHeaderLength];
        WriteRecordHeader(header, record);
        await _output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await _output.WriteAsync(record.Payload, cancellationToken).ConfigureAwait(false);
    }

    private void WriteFileHeader()
    {
        Span<byte> header = stackalloc byte[FileHeaderLength];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], FormatVersion);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], FileHeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(header[12..], 0);
        BinaryPrimitives.WriteInt64BigEndian(header[16..], CreatedAt.ToUnixTimeMilliseconds());
        _output.Write(header);
    }

    private static void WriteRecordHeader(Span<byte> header, BlueTuskProtocolCaptureRecord record)
    {
        header[0] = (byte)record.Direction;
        header[1] = (byte)record.Attributes;
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], RecordHeaderLength);
        BinaryPrimitives.WriteInt64BigEndian(header[4..], checked(record.Elapsed.Ticks / 10));
        BinaryPrimitives.WriteInt32BigEndian(header[12..], record.Payload.Length);
    }

    private static void ValidateRecord(BlueTuskProtocolCaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Enum.IsDefined(record.Direction))
        {
            throw new ArgumentOutOfRangeException(nameof(record), "The capture direction is invalid.");
        }

        if ((record.Attributes & ~(BlueTuskCaptureRecordAttributes.Redacted | BlueTuskCaptureRecordAttributes.Encrypted)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "The capture record contains unsupported flags.");
        }

        if (record.Elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "Capture elapsed time cannot be negative.");
        }
    }
}

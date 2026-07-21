using System.Buffers.Binary;
using BlueTusk.Protocol.Capture;

namespace BlueTusk.Protocol.Tests;

public sealed class BlueTuskProtocolCaptureTests
{
    [Fact]
    public void Writes_a_deterministic_versioned_file_format()
    {
        using var stream = new MemoryStream();
        var writer = new BlueTuskProtocolCaptureWriter(stream, DateTimeOffset.UnixEpoch);

        writer.WriteRecord(
            new BlueTuskProtocolCaptureRecord(
                BlueTuskCaptureDirection.Frontend,
                BlueTuskCaptureRecordAttributes.None,
                TimeSpan.FromMicroseconds(123),
                new byte[] { (byte)'Q' }));

        Assert.Equal(
            "4254504341500D0A00010018000000000000000000000000" +
            "00000010000000000000007B0000000151",
            Convert.ToHexString(stream.ToArray()));
    }

    [Fact]
    public async Task Round_trips_sync_and_async_records()
    {
        using var stream = new MemoryStream();
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_123);
        var writer = new BlueTuskProtocolCaptureWriter(stream, createdAt);
        writer.WriteRecord(
            new BlueTuskProtocolCaptureRecord(
                BlueTuskCaptureDirection.Frontend,
                BlueTuskCaptureRecordAttributes.Redacted,
                TimeSpan.Zero,
                ReadOnlyMemory<byte>.Empty));
        await writer.WriteRecordAsync(
            new BlueTuskProtocolCaptureRecord(
                BlueTuskCaptureDirection.Backend,
                BlueTuskCaptureRecordAttributes.Encrypted,
                TimeSpan.FromMilliseconds(12.345),
                new byte[] { (byte)'Z', 0, 0, 0, 5, (byte)'I' }),
            CancellationToken.None);
        stream.Position = 0;

        var reader = new BlueTuskProtocolCaptureReader(stream);
        var first = reader.ReadRecord();
        var second = await reader.ReadRecordAsync(CancellationToken.None);

        Assert.Equal(createdAt, reader.CreatedAt);
        Assert.Equal(BlueTuskCaptureDirection.Frontend, first!.Direction);
        Assert.Equal(BlueTuskCaptureRecordAttributes.Redacted, first.Attributes);
        Assert.Empty(first.Payload.ToArray());
        Assert.Equal(BlueTuskCaptureDirection.Backend, second!.Direction);
        Assert.Equal(BlueTuskCaptureRecordAttributes.Encrypted, second.Attributes);
        Assert.Equal(TimeSpan.FromMilliseconds(12.345), second.Elapsed);
        Assert.Equal(new byte[] { (byte)'Z', 0, 0, 0, 5, (byte)'I' }, second.Payload.ToArray());
        Assert.Null(await reader.ReadRecordAsync(CancellationToken.None));
    }

    [Fact]
    public void Rejects_unknown_files_and_versions()
    {
        var invalidMagic = CreateHeader();
        invalidMagic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(
            () => new BlueTuskProtocolCaptureReader(new MemoryStream(invalidMagic)));

        var invalidVersion = CreateHeader();
        BinaryPrimitives.WriteUInt16BigEndian(invalidVersion.AsSpan(8), 2);
        Assert.Throws<InvalidDataException>(
            () => new BlueTuskProtocolCaptureReader(new MemoryStream(invalidVersion)));
    }

    [Fact]
    public void Rejects_oversized_payloads_before_allocating()
    {
        using var stream = new MemoryStream();
        var writer = new BlueTuskProtocolCaptureWriter(stream, DateTimeOffset.UnixEpoch);
        writer.WriteRecord(
            new BlueTuskProtocolCaptureRecord(
                BlueTuskCaptureDirection.Backend,
                BlueTuskCaptureRecordAttributes.None,
                TimeSpan.Zero,
                new byte[4]));
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(36), 1024);
        var reader = new BlueTuskProtocolCaptureReader(new MemoryStream(bytes), maximumPayloadLength: 8);

        var exception = Assert.Throws<InvalidDataException>(() => reader.ReadRecord());

        Assert.Contains("configured limit 8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_truncated_records()
    {
        using var stream = new MemoryStream();
        var writer = new BlueTuskProtocolCaptureWriter(stream, DateTimeOffset.UnixEpoch);
        writer.WriteRecord(
            new BlueTuskProtocolCaptureRecord(
                BlueTuskCaptureDirection.Backend,
                BlueTuskCaptureRecordAttributes.None,
                TimeSpan.Zero,
                new byte[4]));
        var truncated = stream.ToArray()[..^2];
        var reader = new BlueTuskProtocolCaptureReader(new MemoryStream(truncated));

        Assert.Throws<EndOfStreamException>(() => reader.ReadRecord());
    }

    private static byte[] CreateHeader()
    {
        using var stream = new MemoryStream();
        _ = new BlueTuskProtocolCaptureWriter(stream, DateTimeOffset.UnixEpoch);
        return stream.ToArray();
    }
}

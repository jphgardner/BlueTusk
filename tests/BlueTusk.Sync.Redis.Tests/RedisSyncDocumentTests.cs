using System.Security.Cryptography;

namespace BlueTusk.Sync.Redis.Tests;

public sealed class RedisSyncDocumentTests
{
    [Fact]
    public void Document_format_round_trips_binary_content_and_rejects_corruption()
    {
        var value = RedisSyncDocumentCodec.Encode(
            "stable-change-id",
            new byte[] { 0, 1, 2, 255 },
            "application/octet-stream",
            "tenant-1");

        var document = RedisSyncDocumentReader.Decode(value);

        Assert.Equal("stable-change-id", document.StableSourceId);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, document.Content.ToArray());
        Assert.Equal("application/octet-stream", document.ContentType);
        Assert.Equal("tenant-1", document.PartitionKey);

        value[value.Length / 2] ^= 0x20;
        _ = Assert.Throws<RedisSyncDocumentException>(
            () => RedisSyncDocumentReader.Decode(value));
    }

    [Fact]
    public void Reader_rejects_an_integrity_valid_future_format()
    {
        var value = RedisSyncDocumentCodec.Encode(
            "stable-change-id",
            "{}"u8.ToArray(),
            "application/json",
            null);

        value[4] = checked((byte)(RedisSyncDocumentReader.CurrentFormatVersion + 1));
        SHA256.HashData(value.AsSpan(0, value.Length - 32))
            .CopyTo(value.AsSpan(value.Length - 32));

        var exception = Assert.Throws<RedisSyncDocumentException>(
            () => RedisSyncDocumentReader.Decode(value));
        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

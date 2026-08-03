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
}

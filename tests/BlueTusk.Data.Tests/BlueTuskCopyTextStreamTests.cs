using System.Text;
using BlueTusk.Data.Copy;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskCopyTextStreamTests
{
    [Fact]
    public async Task Reader_stream_encodes_utf8_across_one_byte_reads()
    {
        const string expected = "ASCII, éléphant 🐘\n";
        using var source = new StringReader(expected);
        await using var stream = new BlueTuskCopyTextReaderStream(source);
        using var destination = new MemoryStream();
        var buffer = new byte[1];

        while (await stream.ReadAsync(buffer, CancellationToken.None) != 0)
        {
            destination.WriteByte(buffer[0]);
        }

        Assert.Equal(Encoding.UTF8.GetBytes(expected), destination.ToArray());
    }

    [Fact]
    public async Task Writer_stream_decodes_fragmented_utf8_without_owning_writer()
    {
        const string expected = "ASCII, éléphant 🐘\n";
        var destination = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        await using var stream = new BlueTuskCopyTextWriterStream(destination);

        foreach (var value in Encoding.UTF8.GetBytes(expected))
        {
            await stream.WriteAsync(new byte[] { value }, CancellationToken.None);
        }

        await stream.CompleteAsync(CancellationToken.None);
        Assert.Equal(expected, destination.ToString());
        destination.Write("still open");
    }

    [Fact]
    public async Task Writer_stream_rejects_incomplete_utf8_when_completed()
    {
        using var destination = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        await using var stream = new BlueTuskCopyTextWriterStream(destination);
        await stream.WriteAsync(new byte[] { 0xF0, 0x9F }, CancellationToken.None);

        await Assert.ThrowsAsync<DecoderFallbackException>(
            () => stream.CompleteAsync(CancellationToken.None).AsTask());
    }
}

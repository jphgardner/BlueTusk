using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.Protocol.Tests;

public sealed class BlueTuskFrontendMessageWriterTests
{
    [Fact]
    public void Writes_a_protocol_30_startup_message()
    {
        var output = new ArrayBufferWriter<byte>();

        BlueTuskFrontendMessageWriter.WriteStartupMessage(
            output,
            new Dictionary<string, string>
            {
                ["user"] = "alice",
                ["database"] = "app",
            });

        var message = output.WrittenSpan;
        Assert.Equal(message.Length, BinaryPrimitives.ReadInt32BigEndian(message));
        Assert.Equal(BlueTuskFrontendMessageWriter.ProtocolVersion30, BinaryPrimitives.ReadInt32BigEndian(message[4..]));
        Assert.Equal("user\0alice\0database\0app\0\0", Encoding.UTF8.GetString(message[8..]));
    }

    [Fact]
    public void Writes_a_simple_query_message()
    {
        var output = new ArrayBufferWriter<byte>();

        BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, "SELECT 1");

        Assert.Equal((byte)'Q', output.WrittenSpan[0]);
        Assert.Equal(13, BinaryPrimitives.ReadInt32BigEndian(output.WrittenSpan[1..]));
        Assert.Equal("SELECT 1\0", Encoding.UTF8.GetString(output.WrittenSpan[5..]));
    }

    [Fact]
    public void Rejects_embedded_nulls()
    {
        Assert.Throws<ArgumentException>(
            () => BlueTuskFrontendMessageWriter.WriteSimpleQuery(new ArrayBufferWriter<byte>(), "SELECT\0 1"));
    }
}


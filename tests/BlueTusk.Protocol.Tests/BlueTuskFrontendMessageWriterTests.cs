using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.Protocol.Tests;

public sealed class BlueTuskFrontendMessageWriterTests
{
    [Fact]
    public void Writes_an_ssl_request()
    {
        var output = new ArrayBufferWriter<byte>();

        BlueTuskFrontendMessageWriter.WriteSslRequest(output);

        Assert.Equal(8, BinaryPrimitives.ReadInt32BigEndian(output.WrittenSpan));
        Assert.Equal(80877103, BinaryPrimitives.ReadInt32BigEndian(output.WrittenSpan[4..]));
    }

    [Fact]
    public void Writes_a_cancel_request()
    {
        var output = new ArrayBufferWriter<byte>();

        BlueTuskFrontendMessageWriter.WriteCancelRequest(output, new BlueTuskBackendKeyData(123, 456));

        Assert.Equal("0000001004D2162E0000007B000001C8", Convert.ToHexString(output.WrittenSpan));
    }

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

    [Fact]
    public void Writes_sasl_initial_and_continuation_responses()
    {
        var initial = new ArrayBufferWriter<byte>();
        var continuation = new ArrayBufferWriter<byte>();

        BlueTuskFrontendMessageWriter.WriteSaslInitialResponse(initial, "SCRAM-SHA-256", "n,,n=user,r=nonce");
        BlueTuskFrontendMessageWriter.WriteSaslResponse(continuation, "c=biws,r=nonce,p=proof");

        Assert.Equal((byte)'p', initial.WrittenSpan[0]);
        Assert.Equal(initial.WrittenCount - 1, BinaryPrimitives.ReadInt32BigEndian(initial.WrittenSpan[1..]));
        Assert.Contains("SCRAM-SHA-256\0", Encoding.UTF8.GetString(initial.WrittenSpan), StringComparison.Ordinal);
        Assert.Equal((byte)'p', continuation.WrittenSpan[0]);
        Assert.Equal(continuation.WrittenCount - 1, BinaryPrimitives.ReadInt32BigEndian(continuation.WrittenSpan[1..]));
    }

    [Fact]
    public void Writes_an_extended_query_message_sequence()
    {
        var output = new ArrayBufferWriter<byte>();

        BlueTuskFrontendMessageWriter.WriteParse(output, string.Empty, "SELECT $1::int4", [23]);
        var parseLength = BinaryPrimitives.ReadInt32BigEndian(output.WrittenSpan[1..]);
        var bindOffset = output.WrittenCount;
        BlueTuskFrontendMessageWriter.WriteBind(
            output,
            string.Empty,
            string.Empty,
            [new BlueTuskBindParameter(1, new byte[] { 0, 0, 0, 42 }), new BlueTuskBindParameter(0, null)]);
        var describeOffset = output.WrittenCount;
        BlueTuskFrontendMessageWriter.WriteDescribePortal(output, string.Empty);
        var executeOffset = output.WrittenCount;
        BlueTuskFrontendMessageWriter.WriteExecute(output, string.Empty);
        var syncOffset = output.WrittenCount;
        BlueTuskFrontendMessageWriter.WriteSync(output);

        Assert.Equal((byte)'P', output.WrittenSpan[0]);
        Assert.Equal(bindOffset - 1, parseLength);
        Assert.Equal((byte)'B', output.WrittenSpan[bindOffset]);
        Assert.Equal(describeOffset - bindOffset - 1, BinaryPrimitives.ReadInt32BigEndian(output.WrittenSpan[(bindOffset + 1)..]));
        Assert.Equal((byte)'D', output.WrittenSpan[describeOffset]);
        Assert.Equal((byte)'E', output.WrittenSpan[executeOffset]);
        Assert.Equal((byte)'S', output.WrittenSpan[syncOffset]);
    }
}

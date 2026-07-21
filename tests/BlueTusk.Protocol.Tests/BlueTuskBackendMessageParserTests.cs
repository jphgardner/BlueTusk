using System.Buffers;
using System.Buffers.Binary;

namespace BlueTusk.Protocol.Tests;

public sealed class BlueTuskBackendMessageParserTests
{
    [Fact]
    public void Parses_a_message_fragmented_at_every_byte_boundary()
    {
        var frame = FakePostgreSqlMessageStream.BackendMessage((byte)'Z', [73]);
        var parser = new BlueTuskBackendMessageParser();

        foreach (var candidate in FakePostgreSqlMessageStream.EveryTwoSegmentSplit(frame))
        {
            var buffer = candidate;

            Assert.True(parser.TryParse(ref buffer, out var message));
            Assert.Equal('Z', message.Identifier);
            Assert.Equal([73], message.ToPayloadArray());
            Assert.True(buffer.IsEmpty);
        }
    }

    [Fact]
    public void Incomplete_message_does_not_consume_input()
    {
        var bytes = FakePostgreSqlMessageStream.BackendMessage((byte)'D', [1, 2, 3]);
        var buffer = new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1));
        var originalLength = buffer.Length;

        Assert.False(new BlueTuskBackendMessageParser().TryParse(ref buffer, out _));
        Assert.Equal(originalLength, buffer.Length);
    }

    [Fact]
    public void Parses_combined_messages_one_at_a_time()
    {
        var first = FakePostgreSqlMessageStream.BackendMessage((byte)'1', []);
        var second = FakePostgreSqlMessageStream.BackendMessage((byte)'Z', [(byte)'I']);
        var bytes = first.Concat(second).ToArray();
        var buffer = new ReadOnlySequence<byte>(bytes);
        var parser = new BlueTuskBackendMessageParser();

        Assert.True(parser.TryParse(ref buffer, out var parsedFirst));
        Assert.True(parser.TryParse(ref buffer, out var parsedSecond));

        Assert.Equal('1', parsedFirst.Identifier);
        Assert.Equal('Z', parsedSecond.Identifier);
        Assert.True(buffer.IsEmpty);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Rejects_invalid_message_lengths(int length)
    {
        var bytes = new byte[5];
        bytes[0] = (byte)'E';
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1), length);
        var buffer = new ReadOnlySequence<byte>(bytes);

        Assert.Throws<BlueTuskProtocolException>(
            () => new BlueTuskBackendMessageParser().TryParse(ref buffer, out _));
    }

    [Fact]
    public void Rejects_messages_over_the_configured_limit_before_buffering_the_payload()
    {
        var bytes = new byte[5];
        bytes[0] = (byte)'D';
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1), 1025);
        var buffer = new ReadOnlySequence<byte>(bytes);

        Assert.Throws<BlueTuskProtocolException>(
            () => new BlueTuskBackendMessageParser(1024).TryParse(ref buffer, out _));
    }
}


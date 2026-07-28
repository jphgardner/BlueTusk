using System.Buffers.Binary;

namespace BlueTusk.Replication.Tests;

public sealed class BlueTuskReplicationWireProtocolTests
{
    [Fact]
    public void Decodes_xlog_data_without_copying_its_wal_payload()
    {
        var bytes = new byte[28];
        bytes[0] = (byte)'w';
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(1), 100);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(9), 200);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(17), 1_500_000);
        bytes[25] = 1;
        bytes[26] = 2;
        bytes[27] = 3;

        var message = Assert.IsType<BlueTuskXLogData>(
            BlueTuskReplicationWireProtocol.Decode(bytes));

        Assert.Equal(100UL, message.WalStart.Value);
        Assert.Equal(103UL, message.WalEnd.Value);
        Assert.Equal(200UL, message.ServerWalEnd.Value);
        Assert.Equal(
            new DateTimeOffset(2000, 1, 1, 0, 0, 1, 500, TimeSpan.Zero),
            message.ServerClock);
        Assert.Equal([1, 2, 3], message.Data.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decodes_primary_keepalive(bool replyRequested)
    {
        var bytes = new byte[18];
        bytes[0] = (byte)'k';
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(1), 42);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(9), -1_000_000);
        bytes[17] = replyRequested ? (byte)1 : (byte)0;

        var message = Assert.IsType<BlueTuskPrimaryKeepalive>(
            BlueTuskReplicationWireProtocol.Decode(bytes));

        Assert.Equal(42UL, message.ServerWalEnd.Value);
        Assert.Equal(
            new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero),
            message.ServerClock);
        Assert.Equal(replyRequested, message.ReplyRequested);
    }

    [Fact]
    public void Encodes_standby_status_in_network_byte_order()
    {
        var payload = BlueTuskReplicationWireProtocol.EncodeStandbyStatus(
            new BlueTuskStandbyStatus(
                new BlueTuskLogSequenceNumber(11),
                new BlueTuskLogSequenceNumber(10),
                new BlueTuskLogSequenceNumber(9),
                ReplyRequested: true),
            new DateTimeOffset(2000, 1, 1, 0, 0, 2, TimeSpan.Zero));

        Assert.Equal(34, payload.Length);
        Assert.Equal((byte)'r', payload[0]);
        Assert.Equal(11UL, BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(1)));
        Assert.Equal(10UL, BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(9)));
        Assert.Equal(9UL, BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(17)));
        Assert.Equal(2_000_000L, BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(25)));
        Assert.Equal(1, payload[33]);
    }

    [Fact]
    public void Encodes_hot_standby_feedback_in_network_byte_order()
    {
        var payload = BlueTuskReplicationWireProtocol.EncodeHotStandbyFeedback(
            new BlueTuskHotStandbyFeedback(1, 2, 3, 4),
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(25, payload.Length);
        Assert.Equal((byte)'h', payload[0]);
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(9)));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(13)));
        Assert.Equal(3U, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(17)));
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(21)));
    }

    [Fact]
    public void Rejects_malformed_replication_messages()
    {
        Assert.Throws<BlueTuskReplicationProtocolException>(
            () => BlueTuskReplicationWireProtocol.Decode(ReadOnlyMemory<byte>.Empty));
        Assert.Throws<BlueTuskReplicationProtocolException>(
            () => BlueTuskReplicationWireProtocol.Decode(new byte[] { (byte)'w' }));
        Assert.Throws<BlueTuskReplicationProtocolException>(
            () => BlueTuskReplicationWireProtocol.Decode(new byte[] { (byte)'?' }));
    }
}

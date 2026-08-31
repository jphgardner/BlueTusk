using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace BlueTusk.Replication;

internal static class BlueTuskReplicationWireProtocol
{
    private const int XLogDataHeaderLength = 25;
    private const int KeepaliveLength = 18;
    private const int StandbyStatusLength = 34;
    private const int HotStandbyFeedbackLength = 25;
    private static readonly DateTimeOffset PostgreSqlEpoch =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BlueTuskReplicationMessage Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new BlueTuskReplicationProtocolException(
                "A replication CopyData payload cannot be empty.");
        }

        var code = payload.Span[0];
        if (code == (byte)'w')
        {
            return DecodeXLogData(payload);
        }

        if (code == (byte)'k')
        {
            return DecodeKeepalive(payload.Span);
        }

        throw new BlueTuskReplicationProtocolException(
            $"Unknown replication message code 0x{code:X2}.");
    }

    public static byte[] EncodeStandbyStatus(
        BlueTuskStandbyStatus status,
        DateTimeOffset clientClock)
    {
        var payload = new byte[StandbyStatusLength];
        payload[0] = (byte)'r';
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1), status.Written.Value);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(9), status.Flushed.Value);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(17), status.Applied.Value);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(25), ToPostgreSqlMicroseconds(clientClock));
        payload[33] = status.ReplyRequested ? (byte)1 : (byte)0;
        return payload;
    }

    public static byte[] EncodeHotStandbyFeedback(
        BlueTuskHotStandbyFeedback feedback,
        DateTimeOffset clientClock)
    {
        var payload = new byte[HotStandbyFeedbackLength];
        payload[0] = (byte)'h';
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), ToPostgreSqlMicroseconds(clientClock));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(9), feedback.Xmin);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(13), feedback.XminEpoch);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(17), feedback.CatalogXmin);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(21), feedback.CatalogXminEpoch);
        return payload;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static BlueTuskXLogData DecodeXLogData(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < XLogDataHeaderLength)
        {
            throw new BlueTuskReplicationProtocolException(
                $"XLogData requires at least {XLogDataHeaderLength} bytes.");
        }

        var span = payload.Span;
        return new BlueTuskXLogData(
                new BlueTuskLogSequenceNumber(BinaryPrimitives.ReadUInt64BigEndian(span[1..])),
                new BlueTuskLogSequenceNumber(BinaryPrimitives.ReadUInt64BigEndian(span[9..])),
                FromPostgreSqlMicroseconds(BinaryPrimitives.ReadInt64BigEndian(span[17..])),
                payload[XLogDataHeaderLength..])
            .MarkDataOwned();
    }

    private static BlueTuskPrimaryKeepalive DecodeKeepalive(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != KeepaliveLength)
        {
            throw new BlueTuskReplicationProtocolException(
                $"A primary keepalive must contain exactly {KeepaliveLength} bytes.");
        }

        var reply = payload[17] switch
        {
            0 => false,
            1 => true,
            var value => throw new BlueTuskReplicationProtocolException(
                $"A keepalive reply flag must be 0 or 1, but was {value}."),
        };
        return new BlueTuskPrimaryKeepalive(
            new BlueTuskLogSequenceNumber(BinaryPrimitives.ReadUInt64BigEndian(payload[1..])),
            FromPostgreSqlMicroseconds(BinaryPrimitives.ReadInt64BigEndian(payload[9..])),
            reply);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DateTimeOffset FromPostgreSqlMicroseconds(long microseconds)
    {
        if (microseconds == 0)
        {
            return PostgreSqlEpoch;
        }

        try
        {
            var ticks = checked(
                PostgreSqlEpoch.UtcTicks +
                checked(microseconds * TimeSpan.TicksPerMicrosecond));
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new BlueTuskReplicationProtocolException(
                "A replication timestamp is outside the supported DateTimeOffset range.",
                exception);
        }
        catch (OverflowException exception)
        {
            throw new BlueTuskReplicationProtocolException(
                "A replication timestamp is outside the supported DateTimeOffset range.",
                exception);
        }
    }

    private static long ToPostgreSqlMicroseconds(DateTimeOffset value) =>
        (value.UtcTicks - PostgreSqlEpoch.UtcTicks) /
        TimeSpan.TicksPerMicrosecond;
}

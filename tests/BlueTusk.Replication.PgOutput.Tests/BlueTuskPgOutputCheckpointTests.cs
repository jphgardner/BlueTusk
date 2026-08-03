using BlueTusk.TypeSystem;

namespace BlueTusk.Replication.PgOutput.Tests;

public sealed class BlueTuskPgOutputCheckpointTests
{
    [Fact]
    public void Terminal_messages_expose_exact_transaction_end_positions()
    {
        var xLogData = new BlueTuskXLogData(
            new BlueTuskLogSequenceNumber(10),
            new BlueTuskLogSequenceNumber(100),
            DateTimeOffset.UnixEpoch,
            new byte[] { 1, 2, 3 });
        BlueTuskPgOutputMessage[] terminalMessages =
        [
            new BlueTuskPgOutputCommit(
                new BlueTuskLogSequenceNumber(80),
                new BlueTuskLogSequenceNumber(90),
                DateTimeOffset.UnixEpoch),
            new BlueTuskPgOutputStreamCommit(
                1,
                new BlueTuskLogSequenceNumber(80),
                new BlueTuskLogSequenceNumber(91),
                DateTimeOffset.UnixEpoch),
            new BlueTuskPgOutputPrepare(
                new BlueTuskLogSequenceNumber(80),
                new BlueTuskLogSequenceNumber(92),
                DateTimeOffset.UnixEpoch,
                1,
                "transaction"),
            new BlueTuskPgOutputCommitPrepared(
                new BlueTuskLogSequenceNumber(80),
                new BlueTuskLogSequenceNumber(93),
                DateTimeOffset.UnixEpoch,
                1,
                "transaction"),
            new BlueTuskPgOutputRollbackPrepared(
                new BlueTuskLogSequenceNumber(80),
                new BlueTuskLogSequenceNumber(94),
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                1,
                "transaction"),
            new BlueTuskPgOutputStreamPrepare(
                new BlueTuskLogSequenceNumber(80),
                new BlueTuskLogSequenceNumber(95),
                DateTimeOffset.UnixEpoch,
                1,
                "transaction"),
        ];
        BlueTuskLogSequenceNumber[] expectedPositions =
        [
            new(90),
            new(91),
            new(92),
            new(93),
            new(94),
            new(95),
        ];
        var insert = new BlueTuskPgOutputEnvelope(
            xLogData,
            new BlueTuskPgOutputInsert(
                StreamingTransactionId: null,
                RelationId: 1,
                new BlueTuskPgOutputTuple([])));

        for (var index = 0; index < terminalMessages.Length; index++)
        {
            var envelope = new BlueTuskPgOutputEnvelope(xLogData, terminalMessages[index]);

            Assert.True(envelope.TryGetTransactionEndPosition(out var position));
            Assert.Equal(expectedPositions[index], position);
            Assert.NotEqual(xLogData.WalEnd, position);
        }

        Assert.False(insert.TryGetTransactionEndPosition(out _));
    }
}

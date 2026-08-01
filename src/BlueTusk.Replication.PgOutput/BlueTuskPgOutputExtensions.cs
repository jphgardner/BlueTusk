using System.Runtime.CompilerServices;

namespace BlueTusk.Replication.PgOutput;

public static class BlueTuskPgOutputExtensions
{
    /// <summary>
    /// Gets the exact transaction-end position from a terminal pgoutput message.
    /// Persist this position only after the corresponding application work is durable.
    /// </summary>
    public static bool TryGetTransactionEndPosition(
        this BlueTuskPgOutputEnvelope envelope,
        out BlueTuskLogSequenceNumber position)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        position = envelope.Message switch
        {
            BlueTuskPgOutputCommit commit => commit.TransactionEndPosition,
            BlueTuskPgOutputStreamCommit commit => commit.TransactionEndPosition,
            BlueTuskPgOutputPrepare prepare => prepare.TransactionEndPosition,
            BlueTuskPgOutputCommitPrepared commit => commit.TransactionEndPosition,
            BlueTuskPgOutputRollbackPrepared rollback => rollback.RollbackEndPosition,
            BlueTuskPgOutputStreamPrepare prepare => prepare.TransactionEndPosition,
            _ => default,
        };
        return position != BlueTuskLogSequenceNumber.Zero;
    }

    /// <summary>Decodes XLogData envelopes in a replication stream with pgoutput.</summary>
    public static async IAsyncEnumerable<BlueTuskPgOutputEnvelope> DecodePgOutputAsync(
        this IAsyncEnumerable<BlueTuskReplicationMessage> source,
        BlueTuskPgOutputDecoderOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var decoder = new BlueTuskPgOutputDecoder(
            options ?? new BlueTuskPgOutputDecoderOptions());
        await foreach (var message in source
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (message is BlueTuskXLogData xLogData)
            {
                yield return decoder.Decode(xLogData);
            }
        }
    }
}

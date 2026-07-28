using System.Runtime.CompilerServices;

namespace BlueTusk.Replication.PgOutput;

public static class BlueTuskPgOutputExtensions
{
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

using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueTusk.Streams;
using BlueTusk.Sync;
using BlueTusk.Sync.Nats;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;

namespace BlueTusk.Benchmarks;

internal static class NatsSyncCaptureVerification
{
    public static async Task RunAsync(int changes, string sourceTransactions, string role)
    {
        var source = sourceTransactions.Split(',').Select(value =>
        {
            var fields = value.Split(':');
            return (Id: uint.Parse(fields[0], CultureInfo.InvariantCulture),
                Commit: DateTimeOffset.UnixEpoch.AddTicks(checked(long.Parse(fields[1], CultureInfo.InvariantCulture) * 10)));
        }).ToArray();
        if (changes is not (1 or 100 or 1_000) || source.Length == 0 || role is not ("candidate" or "reference") ||
            source.Select(transaction => transaction.Id).Distinct().Count() != source.Length)
        {
            throw new InvalidDataException("The NATS verifier requires each PostgreSQL transaction in source order.");
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var cancellationToken = deadline.Token;
        await using var client = SyncCaptureSink.CreateNatsClient();
        var jetStream = client.CreateJetStreamContext();
        var stream = await jetStream.GetStreamAsync(SyncCaptureSink.NatsStream, cancellationToken: cancellationToken);
        if (stream.Info.Config.Storage != StreamConfigStorage.File || stream.Info.State.Messages != source.Length)
        {
            throw new InvalidDataException("JetStream did not restore exactly one file-backed envelope per PostgreSQL transaction.");
        }
        if (role == "reference")
        {
            var staged = await jetStream.GetStreamAsync(SyncCaptureSink.NatsStageStream, cancellationToken: cancellationToken);
            if (staged.Info.State.Messages != 0)
            {
                throw new InvalidDataException("JetStream retained partial fragments after the final transaction was published.");
            }
        }

        var expectedSubject = $"{SyncCaptureSink.NatsSubjectPrefix}.transaction";
        var consumer = await jetStream.CreateOrderedConsumerAsync(SyncCaptureSink.NatsStream, cancellationToken: cancellationToken);
        var count = 0;
        ulong previousPosition = 0;
        await foreach (var message in consumer.FetchAsync<byte[]>(new() { MaxMsgs = source.Length, Expires = TimeSpan.FromSeconds(15) },
            cancellationToken: cancellationToken))
        {
            if (message.Subject != expectedSubject || message.Data is null || count >= source.Length)
            {
                throw new InvalidDataException("JetStream delivered an unexpected subject or message.");
            }
            var envelope = NatsSyncEnvelopeReader.Decode(message.Data);
            var expected = source[count];
            if (envelope.Kind != NatsSyncEnvelopeKind.Transaction || envelope.PipelineId != SyncCaptureSink.PipelineId ||
                envelope.Transaction is not { Outcome: ChangeTransactionOutcome.Committed } transaction ||
                transaction.TransactionId != expected.Id || transaction.CommitTimestamp != expected.Commit ||
                transaction.CommitEndPosition.Value <= previousPosition || envelope.Mutations.Count != changes)
            {
                throw new InvalidDataException("A recovered JetStream envelope differs from its source transaction.");
            }

            for (var index = 0; index < changes; index++)
            {
                var mutation = envelope.Mutations[index];
                var key = (count * (long)changes + index + 1).ToString(CultureInfo.InvariantCulture);
                var stableId = $"{envelope.Source.Fingerprint}:{transaction.CommitEndPosition.Value:x16}:{expected.Id:x8}:{index:x8}";
                if (mutation is not { Kind: SyncMutationKind.Upsert, Collection: SyncCaptureSink.Collection, ContentType: "application/json", PartitionKey: null } ||
                    mutation.Key != key || mutation.StableId != stableId || Encoding.UTF8.GetString(mutation.Content.Span) != $"{{\"id\":{key}}}")
                {
                    throw new InvalidDataException("JetStream did not recover the complete ordered transaction payload.");
                }
            }
            previousPosition = transaction.CommitEndPosition.Value;
            count++;
        }
        if (count != source.Length)
        {
            throw new EndOfStreamException("JetStream did not recover the entire transaction sequence.");
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            transactions = count,
            documents = checked(count * changes),
            finalSourceTransactionId = source[^1].Id,
            recoveredFromJetStream = true,
        }));
    }
}

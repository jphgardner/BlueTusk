using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueTusk.Streams;
using BlueTusk.Sync;
using BlueTusk.Sync.Kafka;
using Confluent.Kafka;

namespace BlueTusk.Benchmarks;

internal static class KafkaSyncCaptureVerification
{
    public static void Run(int changes, string sourceTransactions, string role)
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
            throw new InvalidDataException("The Kafka verifier requires the complete ordered PostgreSQL transaction sequence.");
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var endpoint = SyncCaptureSink.KafkaEndpoint();
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = endpoint,
            GroupId = "sync-capture-verifier",
            EnableAutoCommit = false,
            EnablePartitionEof = true,
            IsolationLevel = IsolationLevel.ReadCommitted,
            FetchWaitMaxMs = 25,
        }).Build();
        var messages = ReadTopic(consumer, "events", deadline.Token);
        var receipts = ReadTopic(consumer, "state", deadline.Token);
        if (messages.Count != source.Length || receipts.Count != source.Length + 1 || receipts[0].Key != "configuration")
        {
            throw new InvalidDataException("Kafka did not recover the expected committed envelopes and checkpoints.");
        }

        using var configuration = JsonDocument.Parse(receipts[0].Value);
        var transform = SyncTransformVersion.Create("sync-capture", "1");
        var sourceFingerprint = configuration.RootElement.GetProperty("sourceFingerprint").GetString();
        if (configuration.RootElement.GetProperty("pipelineId").GetString() != SyncCaptureSink.PipelineId ||
            configuration.RootElement.GetProperty("transformFingerprint").GetString() != transform.Fingerprint)
        {
            throw new InvalidDataException("Kafka recovered the wrong pipeline configuration.");
        }

        var deliveredIds = new HashSet<string>(StringComparer.Ordinal);
        ulong previousPosition = 0;
        for (var batch = 0; batch < messages.Count; batch++)
        {
            var message = messages[batch];
            var receipt = receipts[batch + 1];
            using var envelope = JsonDocument.Parse(message.Value);
            var root = envelope.RootElement;
            var transaction = root.GetProperty("transaction");
            var position = transaction.GetProperty("commitEndPosition").GetString()!;
            var lsn = ulong.Parse(position, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            var expected = source[batch];
            var origin = root.GetProperty("source");
            var identity = new ChangeSourceIdentity(origin.GetProperty("systemIdentifier").GetString()!,
                origin.GetProperty("databaseName").GetString()!, origin.GetProperty("slotName").GetString()!,
                origin.GetProperty("publicationFingerprint").GetString()!);
            var deliveryId = root.GetProperty("deliveryId").GetString()!;
            var mutations = root.GetProperty("mutations");
            if (root.GetProperty("event").GetString() != "transaction" ||
                root.GetProperty("formatVersion").GetInt32() != KafkaSyncProtocol.CurrentFormatVersion ||
                root.GetProperty("pipelineId").GetString() != SyncCaptureSink.PipelineId ||
                origin.GetProperty("fingerprint").GetString() != identity.Fingerprint || identity.Fingerprint != sourceFingerprint ||
                root.GetProperty("transform").GetProperty("fingerprint").GetString() != transform.Fingerprint ||
                transaction.GetProperty("outcome").GetString() != "Committed" ||
                transaction.GetProperty("transactionId").GetUInt32() != expected.Id ||
                transaction.GetProperty("commitTimestamp").GetDateTimeOffset() != expected.Commit ||
                position != lsn.ToString("x16", CultureInfo.InvariantCulture) || lsn <= previousPosition ||
                !deliveryId.StartsWith("bt1-", StringComparison.Ordinal) || !deliveredIds.Add(deliveryId) ||
                message.Key != deliveryId || Header(message, KafkaSyncProtocol.DeliveryIdHeader) != message.Key ||
                Header(message, KafkaSyncProtocol.EventHeader) != "transaction" ||
                Header(message, KafkaSyncProtocol.TransformFingerprintHeader) != transform.Fingerprint ||
                Header(message, KafkaSyncProtocol.FormatVersionHeader) != "1" ||
                receipt.Key != "position" || Encoding.UTF8.GetString(receipt.Value) != position ||
                mutations.GetArrayLength() != changes)
            {
                throw new InvalidDataException("A recovered Kafka envelope or checkpoint did not match its source transaction.");
            }

            for (var ordinal = 0; ordinal < changes; ordinal++)
            {
                var row = mutations[ordinal];
                var key = (batch * (long)changes + ordinal + 1).ToString(CultureInfo.InvariantCulture);
                var stableId = FormattableString.Invariant($"{sourceFingerprint}:{position}:{expected.Id:x8}:{ordinal}");
                if (row.GetProperty("stableId").GetString() != stableId || row.GetProperty("kind").GetString() != "Upsert" ||
                    row.GetProperty("collection").GetString() != SyncCaptureSink.Collection ||
                    row.GetProperty("key").GetString() != key || row.GetProperty("contentType").GetString() != "application/json" ||
                    Encoding.UTF8.GetString(row.GetProperty("contentBase64").GetBytesFromBase64()) != $"{{\"id\":{key}}}")
                {
                    throw new InvalidDataException("Kafka did not recover every ordered mutation in its original envelope.");
                }
            }
            previousPosition = lsn;
        }

        var fragments = role == "reference" ? ReadTopic(consumer, "stage", deadline.Token).Count : 0;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            transactions = messages.Count,
            documents = checked(messages.Count * changes),
            finalSourceTransactionId = source[^1].Id,
            checkpoint = previousPosition.ToString("x16", CultureInfo.InvariantCulture),
            recoveredFragments = fragments,
            recoveredFromKafka = true,
        }));
    }

    private static string Header(Message<string, byte[]> message, string name) =>
        Encoding.UTF8.GetString(message.Headers.GetLastBytes(name));

    private static List<Message<string, byte[]>> ReadTopic(
        IConsumer<string, byte[]> consumer, string suffix, CancellationToken cancellationToken)
    {
        var partition = new TopicPartition(SyncCaptureSink.KafkaTopicPrefix + "." + suffix, new Partition(0));
        consumer.Assign(new TopicPartitionOffset(partition, Offset.Beginning));
        var messages = new List<Message<string, byte[]>>();
        while (true)
        {
            var record = consumer.Consume(cancellationToken);
            if (record.IsPartitionEOF) return messages;
            messages.Add(record.Message);
        }
    }
}

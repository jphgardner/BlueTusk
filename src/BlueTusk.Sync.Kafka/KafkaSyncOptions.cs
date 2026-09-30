using Confluent.Kafka;

namespace BlueTusk.Sync.Kafka;

public sealed record KafkaSyncOptions
{
    public required string BootstrapServers { get; init; }

    public required string TopicPrefix { get; init; }

    public required string TransactionalId { get; init; }

    public string ClientId { get; init; } = "bluetusk-sync";

    public bool CreateTopics { get; init; } = true;

    public int PartitionCount { get; init; } = 1;

    public short ReplicationFactor { get; init; } = 3;

    public int MaxEnvelopeBytes { get; init; } = 8 * 1024 * 1024;

    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan TransactionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public IReadOnlyDictionary<string, string> ClientConfiguration { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    internal Func<KafkaSyncOptions, IKafkaSyncTransport>? TransportFactory { get; init; }

    internal string DataTopic => TopicPrefix + ".events";

    internal string StateTopic => TopicPrefix + ".state";

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(BootstrapServers);
        ValidateTopicPrefix(TopicPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(TransactionalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientId);
        if (TransactionalId.Length > 240 || ClientId.Length > 240)
        {
            throw new ArgumentOutOfRangeException(
                TransactionalId.Length > 240 ? nameof(TransactionalId) : nameof(ClientId));
        }

        if (PartitionCount != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PartitionCount),
                "BlueTusk requires one Kafka partition per pipeline to preserve commit order.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(ReplicationFactor, (short)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ReplicationFactor, (short)5);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEnvelopeBytes, 1024);
        if (InitializationTimeout < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(InitializationTimeout));
        }

        if (TransactionTimeout < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(TransactionTimeout));
        }

        foreach (var entry in ClientConfiguration)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);
            ArgumentNullException.ThrowIfNull(entry.Value);
        }
    }

    private static void ValidateTopicPrefix(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 230 || value is "." or ".." || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ArgumentException(
                "The topic prefix must use only ASCII letters, digits, '.', '_' or '-' and be at most 230 characters.",
                nameof(value));
        }
    }

    internal ClientConfig BuildClientConfig()
    {
        var values = new Dictionary<string, string>(ClientConfiguration, StringComparer.Ordinal)
        {
            ["bootstrap.servers"] = BootstrapServers,
            ["client.id"] = ClientId,
        };
        return new ClientConfig(values);
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace BlueTusk.Sync.Kafka;

internal sealed record KafkaSyncLoadedState(
    string? PipelineId,
    string? SourceFingerprint,
    string? TransformFingerprint,
    IReadOnlyDictionary<string, string> Checkpoints);

internal sealed record KafkaSyncMessage(
    string EventName,
    string DeliveryId,
    string TransformFingerprint,
    byte[] Payload,
    string CheckpointKey,
    string CheckpointValue,
    IReadOnlyList<string> TombstoneKeys);

internal interface IKafkaSyncTransport : IAsyncDisposable
{
    ValueTask<KafkaSyncLoadedState> LoadAsync(CancellationToken cancellationToken);

    ValueTask InitializeAsync(
        SyncProvisionRequest request,
        bool writeConfiguration,
        CancellationToken cancellationToken);

    ValueTask PublishAsync(KafkaSyncMessage message, CancellationToken cancellationToken);
}

internal sealed class ConfluentKafkaSyncTransport : IKafkaSyncTransport
{
    private const string ConfigurationKey = "configuration";
    private readonly KafkaSyncOptions _options;
    private readonly IProducer<string, byte[]> _producer;
    private bool _initialized;

    internal ConfluentKafkaSyncTransport(KafkaSyncOptions options)
    {
        _options = options;
        var producerConfig = new ProducerConfig(options.BuildClientConfig())
        {
            Acks = Acks.All,
            EnableIdempotence = true,
            TransactionalId = options.TransactionalId,
            TransactionTimeoutMs = checked((int)options.TransactionTimeout.TotalMilliseconds),
            MessageMaxBytes = options.MaxEnvelopeBytes + 64 * 1024,
        };
        _producer = new ProducerBuilder<string, byte[]>(producerConfig).Build();
    }

    public async ValueTask<KafkaSyncLoadedState> LoadAsync(CancellationToken cancellationToken)
    {
        await EnsureTopicsAsync(cancellationToken).ConfigureAwait(false);
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        string? pipelineId = null;
        string? sourceFingerprint = null;
        string? transformFingerprint = null;
        var consumerConfig = new ConsumerConfig(_options.BuildClientConfig())
        {
            GroupId = _options.ClientId + "-state-reader-" + Guid.NewGuid().ToString("N"),
            EnableAutoCommit = false,
            EnablePartitionEof = true,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            IsolationLevel = IsolationLevel.ReadCommitted,
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(consumerConfig).Build();
        var partition = new TopicPartition(_options.StateTopic, new Partition(0));
        var watermark = consumer.QueryWatermarkOffsets(partition, _options.InitializationTimeout);
        if (watermark.High.Value > watermark.Low.Value)
        {
            consumer.Assign(new TopicPartitionOffset(partition, watermark.Low));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_options.InitializationTimeout);
            var nextOffset = watermark.Low.Value;
            while (nextOffset < watermark.High.Value)
            {
                ConsumeResult<string, byte[]> result;
                try
                {
                    result = consumer.Consume(deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new KafkaSyncConfigurationException(
                        $"Timed out while loading compacted state from Kafka topic '{_options.StateTopic}'.");
                }

                if (result.IsPartitionEOF || result.Message?.Key is null)
                {
                    nextOffset = Math.Max(nextOffset, result.Offset.Value);
                    continue;
                }

                nextOffset = Math.Max(nextOffset, result.Offset.Value + 1);

                if (result.Message.Value is null)
                {
                    _ = state.Remove(result.Message.Key);
                    continue;
                }

                if (string.Equals(result.Message.Key, ConfigurationKey, StringComparison.Ordinal))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(result.Message.Value);
                        var root = document.RootElement;
                        pipelineId = root.GetProperty("pipelineId").GetString();
                        sourceFingerprint = root.GetProperty("sourceFingerprint").GetString();
                        transformFingerprint = root.GetProperty("transformFingerprint").GetString();
                    }
                    catch (JsonException exception)
                    {
                        throw new KafkaSyncConfigurationException(
                            $"Kafka state topic '{_options.StateTopic}' contains invalid BlueTusk configuration: {exception.Message}");
                    }
                }
                else
                {
                    state[result.Message.Key] = Encoding.UTF8.GetString(result.Message.Value);
                }
            }
        }

        return new KafkaSyncLoadedState(
            pipelineId,
            sourceFingerprint,
            transformFingerprint,
            state);
    }

    public async ValueTask InitializeAsync(
        SyncProvisionRequest request,
        bool writeConfiguration,
        CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            _producer.InitTransactions(_options.InitializationTimeout);
            _initialized = true;
        }

        if (!writeConfiguration)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = KafkaSyncProtocol.CurrentFormatVersion,
            pipelineId = request.PipelineId,
            sourceFingerprint = request.Source.Fingerprint,
            transformFingerprint = request.Transform.Fingerprint,
        });
        await ExecuteTransactionAsync(
            [new TopicWrite(_options.StateTopic, ConfigurationKey, payload, null)],
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask PublishAsync(KafkaSyncMessage message, CancellationToken cancellationToken)
    {
        var headers = new Headers
        {
            { KafkaSyncProtocol.EventHeader, Encoding.UTF8.GetBytes(message.EventName) },
            { KafkaSyncProtocol.DeliveryIdHeader, Encoding.UTF8.GetBytes(message.DeliveryId) },
            {
                KafkaSyncProtocol.FormatVersionHeader,
                Encoding.ASCII.GetBytes(KafkaSyncProtocol.CurrentFormatVersion.ToString(CultureInfo.InvariantCulture))
            },
            {
                KafkaSyncProtocol.TransformFingerprintHeader,
                Encoding.ASCII.GetBytes(message.TransformFingerprint)
            },
        };
        var writes = new List<TopicWrite>(message.TombstoneKeys.Count + 2)
        {
            new(_options.DataTopic, message.DeliveryId, message.Payload, headers),
            new(
                _options.StateTopic,
                message.CheckpointKey,
                Encoding.UTF8.GetBytes(message.CheckpointValue),
                null),
        };
        writes.AddRange(message.TombstoneKeys.Select(
            key => new TopicWrite(_options.StateTopic, key, null, null)));
        return ExecuteTransactionAsync(writes, cancellationToken);
    }

    private async ValueTask ExecuteTransactionAsync(
        IReadOnlyList<TopicWrite> writes,
        CancellationToken cancellationToken)
    {
        try
        {
            _producer.BeginTransaction();
            foreach (var write in writes)
            {
                _ = await _producer.ProduceAsync(
                    write.Topic,
                    new Message<string, byte[]>
                    {
                        Key = write.Key,
                        Value = write.Value!,
                        Headers = write.Headers,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            _producer.CommitTransaction(_options.TransactionTimeout);
        }
        catch (Exception exception) when (exception is KafkaException or TimeoutException)
        {
            try
            {
                _producer.AbortTransaction(_options.TransactionTimeout);
            }
            catch (KafkaException)
            {
                // The original outcome remains authoritative and ambiguous.
            }

            throw new KafkaSyncDeliveryException(
                "Kafka did not confirm an atomic transaction; the Sync checkpoint was not advanced. Re-provision the destination before retrying so the compacted receipt state resolves an ambiguous commit.",
                exception);
        }
    }

    private async ValueTask EnsureTopicsAsync(CancellationToken cancellationToken)
    {
        using var admin = new AdminClientBuilder(_options.BuildClientConfig()).Build();
        var missing = new List<TopicSpecification>();
        // A topic-specific metadata request can trigger broker-side topic creation when
        // auto.create.topics.enable is true. Read cluster metadata once so BlueTusk owns
        // creation and can apply the required compaction contract to the state topic.
        var metadata = admin.GetMetadata(_options.InitializationTimeout);
        foreach (var topic in new[] { _options.DataTopic, _options.StateTopic })
        {
            var known = metadata.Topics.SingleOrDefault(candidate =>
                string.Equals(candidate.Topic, topic, StringComparison.Ordinal));
            if (known is null || known.Error.Code is ErrorCode.UnknownTopicOrPart)
            {
                missing.Add(new TopicSpecification
                {
                    Name = topic,
                    NumPartitions = _options.PartitionCount,
                    ReplicationFactor = _options.ReplicationFactor,
                    Configs = string.Equals(topic, _options.StateTopic, StringComparison.Ordinal)
                        ? new Dictionary<string, string>
                        {
                            ["cleanup.policy"] = "compact",
                            ["min.compaction.lag.ms"] = "0",
                        }
                        : null,
                });
                continue;
            }

            if (known.Error.IsError)
            {
                throw new KafkaSyncConfigurationException(
                    $"Kafka metadata lookup for '{topic}' failed with {known.Error.Code}.");
            }

            if (known.Partitions.Count != _options.PartitionCount)
            {
                throw new KafkaSyncConfigurationException(
                    $"Kafka topic '{topic}' has {known.Partitions.Count} partitions; exactly {_options.PartitionCount} is required for ordered delivery.");
            }
        }

        if (missing.Count > 0 && !_options.CreateTopics)
        {
            throw new KafkaSyncConfigurationException(
                "One or more required Kafka topics do not exist and automatic creation is disabled.");
        }

        if (missing.Count > 0)
        {
            try
            {
                await admin.CreateTopicsAsync(
                    missing,
                    new CreateTopicsOptions { OperationTimeout = _options.InitializationTimeout })
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (CreateTopicsException exception) when (exception.Results.All(result =>
                result.Error.Code is ErrorCode.TopicAlreadyExists))
            {
                // Concurrent provisioning created the exact named topics.
            }
        }

        metadata = admin.GetMetadata(_options.InitializationTimeout);
        foreach (var topic in new[] { _options.DataTopic, _options.StateTopic })
        {
            var known = metadata.Topics.SingleOrDefault(candidate =>
                string.Equals(candidate.Topic, topic, StringComparison.Ordinal));
            if (known is null || known.Error.IsError)
            {
                throw new KafkaSyncConfigurationException(
                    $"Kafka topic '{topic}' was not available after provisioning.");
            }

            if (known.Partitions.Count != _options.PartitionCount)
            {
                throw new KafkaSyncConfigurationException(
                    $"Kafka topic '{topic}' has {known.Partitions.Count} partitions; exactly {_options.PartitionCount} is required for ordered delivery.");
            }
        }

        var resource = new ConfigResource
        {
            Name = _options.StateTopic,
            Type = ResourceType.Topic,
        };
        var descriptions = await admin.DescribeConfigsAsync([resource])
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var cleanupPolicy = descriptions.Single().Entries["cleanup.policy"].Value;
        if (!cleanupPolicy.Split(',').Contains("compact", StringComparer.Ordinal))
        {
            throw new KafkaSyncConfigurationException(
                $"Kafka state topic '{_options.StateTopic}' must include cleanup.policy=compact.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record TopicWrite(
        string Topic,
        string Key,
        byte[]? Value,
        Headers? Headers);
}

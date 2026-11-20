using System.Buffers.Text;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Amazon.S3;
using BlueTusk.Data;
using BlueTusk.Streams;
using BlueTusk.Sync;
using BlueTusk.Sync.Kafka;
using BlueTusk.Sync.Nats;
using BlueTusk.Sync.OpenSearch;
using BlueTusk.Sync.PostgreSql;
using BlueTusk.Sync.Redis;
using BlueTusk.Sync.S3;
using BlueTusk.TypeSystem;
using NATS.Client.Core;
using NATS.Net;
using StackExchange.Redis;

namespace BlueTusk.Benchmarks;

internal sealed class SyncCaptureSink : IAsyncDisposable
{
    internal const string RedisConnectionVariable = "BLUETUSK_BENCHMARK_REDIS_ENDPOINT";
    internal const string NatsConnectionVariable = "BLUETUSK_BENCHMARK_NATS_ENDPOINT";
    internal const string OpenSearchConnectionVariable = "BLUETUSK_BENCHMARK_OPENSEARCH_ENDPOINT";
    internal const string KafkaConnectionVariable = "BLUETUSK_BENCHMARK_KAFKA_ENDPOINT";
    internal const string PipelineId = "sync_capture";
    internal const string Collection = "streams_capture";
    internal const string NatsStream = "SYNC_CAPTURE";
    internal const string NatsStageStream = "SYNC_CAPTURE_STAGE";
    internal const string NatsSubjectPrefix = "sync.capture";
    internal const string OpenSearchPrefix = "bluetusk-sync";
    internal const string KafkaTopicPrefix = "sync.capture";
    private readonly BlueTuskDataSource? _dataSource;
    private readonly ConnectionMultiplexer? _redis;
    private readonly NatsClient? _nats;
    private readonly HttpClient? _http;
    private readonly KafkaSyncDestination? _kafka;
    private readonly AmazonS3Client? _s3;
    private readonly SyncPipeline _pipeline;

    private SyncCaptureSink(ISyncDestination destination, ChangeSourceIdentity source,
        BlueTuskDataSource? dataSource = null, ConnectionMultiplexer? redis = null, NatsClient? nats = null,
        HttpClient? http = null, AmazonS3Client? s3 = null)
    {
        _dataSource = dataSource;
        _redis = redis;
        _nats = nats;
        _http = http;
        _kafka = destination as KafkaSyncDestination;
        _s3 = s3;
        _pipeline = new SyncPipeline(new() { PipelineId = PipelineId }, source, new CaptureTransform(), destination);
    }

    public static async ValueTask<SyncCaptureSink> CreateAsync(string sink, string sourceConnectionString, ChangeSourceIdentity source)
    {
        if (sink == "s3")
        {
            var client = S3SyncCaptureVerification.Connect();
            return new(new S3SyncDestination(new()
            {
                Client = client,
                BucketName = S3SyncCaptureVerification.Bucket,
                Prefix = S3SyncCaptureVerification.Prefix,
                ServerSideEncryption = ServerSideEncryptionMethod.None,
            }), source, s3: client);
        }
        if (sink == "kafka")
        {
            return new(new KafkaSyncDestination(new()
            {
                BootstrapServers = KafkaEndpoint(),
                TopicPrefix = KafkaTopicPrefix,
                TransactionalId = PipelineId,
                ReplicationFactor = 1,
            }), source);
        }
        if (sink == "opensearch")
        {
            var client = CreateOpenSearchClient();
            return new(new OpenSearchSyncDestination(new() { Client = client, IndexPrefix = OpenSearchPrefix }), source, http: client);
        }
        if (sink == "nats")
        {
            var nats = CreateNatsClient();
            return new(new NatsSyncDestination(new()
            {
                JetStream = nats.CreateJetStreamContext(),
                StreamName = NatsStream,
                SubjectPrefix = NatsSubjectPrefix,
                MaxMessageBytes = 1024 * 1024,
            }), source, nats: nats);
        }
        if (sink == "redis")
        {
            var redis = await ConnectRedisAsync();
            return new(new RedisSyncDestination(new() { Connection = redis }), source, redis: redis);
        }
        var connection = new DbConnectionStringBuilder { ConnectionString = sourceConnectionString };
        connection["Database"] = "bluetusk_sync_sink";
        var dataSource = BlueTuskDataSource.Create(connection.ConnectionString);
        return new(new PostgreSqlSyncDestination(new() { DestinationDataSource = dataSource }), source, dataSource);
    }

    internal static Task<ConnectionMultiplexer> ConnectRedisAsync() =>
        ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable(RedisConnectionVariable) ??
            throw new InvalidOperationException("The capture requires a dedicated Redis fixture endpoint."));

    internal static NatsClient CreateNatsClient() => new(NatsOpts.Default with
    {
        Url = Environment.GetEnvironmentVariable(NatsConnectionVariable) ??
            throw new InvalidOperationException("The capture requires a dedicated NATS fixture endpoint."),
        Name = "sync-performance-capture",
    });

    internal static HttpClient CreateOpenSearchClient() => new()
    {
        BaseAddress = new Uri(Environment.GetEnvironmentVariable(OpenSearchConnectionVariable) ??
            throw new InvalidOperationException("The capture requires a dedicated OpenSearch fixture endpoint.")),
        Timeout = TimeSpan.FromSeconds(30),
    };

    internal static string KafkaEndpoint() => Environment.GetEnvironmentVariable(KafkaConnectionVariable) ??
        throw new InvalidOperationException("The capture requires a dedicated Kafka fixture endpoint.");

    public ValueTask InitializeAsync(CancellationToken cancellationToken) => _pipeline.ProvisionAsync(cancellationToken);

    public async ValueTask ApplyAsync(ChangeTransactionDelivery delivery, CancellationToken cancellationToken)
    {
        await _pipeline.ConsumeTransactionAsync(delivery, cancellationToken);
        if (delivery.State != ChangeDeliveryState.Acknowledged)
        {
            throw new InvalidDataException("Sync did not acknowledge the committed destination transaction.");
        }
    }

    public void VerifyCompleted(int transactions)
    {
        var status = _pipeline.Status;
        if (status is not { State: SyncPipelineState.Running, QuarantinedTransactions: 0, RetryAttempts: 0 } ||
            status.AppliedTransactions != transactions)
        {
            throw new InvalidDataException("Sync did not durably apply each expected transaction on its first attempt.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        if (_dataSource is not null) await _dataSource.DisposeAsync();
        _redis?.Dispose();
        if (_nats is not null) await _nats.DisposeAsync();
        if (_kafka is not null) await _kafka.DisposeAsync();
        _s3?.Dispose();
        _http?.Dispose();
    }

    private sealed class CaptureTransform : ISyncTransform
    {
        private long _nextKey = 1;

        public SyncTransformVersion Version { get; } = SyncTransformVersion.Create("sync-capture", "1");

        public async ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
            ChangeTransaction transaction, CancellationToken cancellationToken = default)
        {
            var mutations = new List<SyncMutation>(transaction.Changes.Count);
            await foreach (var change in transaction.Changes.WithCancellation(cancellationToken))
            {
                if (change is not InsertChange { NewRow: var row } || row.Table is not { Schema: "public", Name: Collection })
                {
                    throw new InvalidDataException("Sync received an unexpected source operation or relation.");
                }
                var id = row["id"];
                if (id is not { State: ChangeColumnState.Value, Encoding: ChangeValueEncoding.Text } ||
                    !Utf8Parser.TryParse(id.Data.Span, out long key, out var consumed) || consumed != id.Data.Length || key != _nextKey)
                {
                    throw new InvalidDataException("Sync received an unexpected source key.");
                }
                var text = key.ToString(CultureInfo.InvariantCulture);
                mutations.Add(new(new ChangeId(transaction.Source, transaction.CommitEndPosition, transaction.TransactionId, mutations.Count),
                    SyncMutationKind.Upsert, Collection, text, Encoding.UTF8.GetBytes($"{{\"id\":{text}}}"), "application/json"));
                _nextKey++;
            }
            if (mutations.Count != transaction.Changes.Count)
            {
                throw new InvalidDataException("Sync did not transform every source change.");
            }
            return mutations;
        }

        public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
            ChangeSnapshotBatch batch, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The Sync benchmark uses committed WAL transactions.");
    }
}

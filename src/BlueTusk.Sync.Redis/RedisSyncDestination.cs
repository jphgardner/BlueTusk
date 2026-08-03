using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;
using StackExchange.Redis;

namespace BlueTusk.Sync.Redis;

public sealed class RedisSyncDestination : ISyncDestination, ISyncQuarantineSink
{
    private const string ProvisionScript = """
        local format = redis.call('HGET', KEYS[1], 'format')
        if not format then
            redis.call('HSET', KEYS[1],
                'format', ARGV[1],
                'pipeline_id', ARGV[2],
                'source', ARGV[3],
                'transform_name', ARGV[4],
                'transform', ARGV[5],
                'generation', '0',
                'snapshot_complete', '0')
            return {0, ''}
        end
        if format ~= ARGV[1] then return {3, format} end
        if redis.call('HGET', KEYS[1], 'pipeline_id') ~= ARGV[2] then return {4, ''} end
        if redis.call('HGET', KEYS[1], 'source') ~= ARGV[3] then return {2, ''} end
        local transform = redis.call('HGET', KEYS[1], 'transform')
        if transform ~= ARGV[5] then return {1, transform} end
        return {0, ''}
        """;

    private const string TransactionScript = """
        if redis.call('HGET', KEYS[1], 'source') ~= ARGV[1] then return 3 end
        if redis.call('HGET', KEYS[1], 'transform') ~= ARGV[2] then return 2 end
        local checkpoint = redis.call('HGET', KEYS[1], 'checkpoint')
        if checkpoint and checkpoint >= ARGV[3] then return 1 end
        local registry_type = redis.call('TYPE', KEYS[2]).ok
        if registry_type ~= 'none' and registry_type ~= 'set' then return 5 end
        for index = 3, #KEYS do
            local key_type = redis.call('TYPE', KEYS[index]).ok
            if key_type ~= 'none' and key_type ~= 'hash' then return 5 end
        end
        local count = tonumber(ARGV[5])
        local offset = 6
        for index = 1, count do
            local key_index = tonumber(ARGV[offset])
            local kind = ARGV[offset + 1]
            local document_key = ARGV[offset + 2]
            local value = ARGV[offset + 3]
            if kind == '0' then
                redis.call('HSET', KEYS[key_index], document_key, value)
                redis.call('SADD', KEYS[2], KEYS[key_index])
            elseif kind == '1' then
                redis.call('HDEL', KEYS[key_index], document_key)
            elseif kind == '2' then
                redis.call('DEL', KEYS[key_index])
                redis.call('SREM', KEYS[2], KEYS[key_index])
            else
                return redis.error_reply('unsupported BlueTusk Sync mutation')
            end
            offset = offset + 4
        end
        redis.call('HSET', KEYS[1], 'checkpoint', ARGV[3], 'transaction_id', ARGV[4])
        redis.call('HINCRBY', KEYS[1], 'generation', 1)
        return 0
        """;

    private const string ResetSnapshotScript = """
        if redis.call('HGET', KEYS[1], 'source') ~= ARGV[1] then return 3 end
        if redis.call('HGET', KEYS[1], 'transform') ~= ARGV[2] then return 2 end
        for index = 3, #KEYS do redis.call('DEL', KEYS[index]) end
        redis.call('DEL', KEYS[2])
        redis.call('HSET', KEYS[1], 'snapshot_epoch', ARGV[3], 'snapshot_complete', '0')
        redis.call('HDEL', KEYS[1], 'checkpoint', 'transaction_id')
        redis.call('HINCRBY', KEYS[1], 'generation', 1)
        return 0
        """;

    private const string SnapshotStateScript = """
        if redis.call('HGET', KEYS[1], 'source') ~= ARGV[1] then return 3 end
        if redis.call('HGET', KEYS[1], 'transform') ~= ARGV[2] then return 2 end
        if redis.call('HGET', KEYS[1], 'snapshot_epoch') ~= ARGV[3] or
           redis.call('HGET', KEYS[1], 'snapshot_complete') ~= '0' then return 4 end
        if ARGV[4] == 'complete' then
            redis.call('HSET', KEYS[1], 'snapshot_complete', '1')
            redis.call('HINCRBY', KEYS[1], 'generation', 1)
        end
        return 0
        """;

    private const string SnapshotBatchScript = """
        if redis.call('HGET', KEYS[1], 'source') ~= ARGV[1] then return 3 end
        if redis.call('HGET', KEYS[1], 'transform') ~= ARGV[2] then return 2 end
        if redis.call('HGET', KEYS[1], 'snapshot_epoch') ~= ARGV[3] or
           redis.call('HGET', KEYS[1], 'snapshot_complete') ~= '0' then return 4 end
        local registry_type = redis.call('TYPE', KEYS[2]).ok
        if registry_type ~= 'none' and registry_type ~= 'set' then return 5 end
        for index = 3, #KEYS do
            local key_type = redis.call('TYPE', KEYS[index]).ok
            if key_type ~= 'none' and key_type ~= 'hash' then return 5 end
        end
        local count = tonumber(ARGV[4])
        local offset = 5
        for index = 1, count do
            local key_index = tonumber(ARGV[offset])
            redis.call('HSET', KEYS[key_index], ARGV[offset + 1], ARGV[offset + 2])
            redis.call('SADD', KEYS[2], KEYS[key_index])
            offset = offset + 3
        end
        return 0
        """;

    private const int CurrentFormatVersion = 1;

    private readonly RedisSyncOptions _options;
    private readonly IDatabase _database;
    private readonly ConcurrentDictionary<string, PipelineRuntime> _pipelines =
        new(StringComparer.Ordinal);

    public RedisSyncDestination(RedisSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _database = options.Connection.GetDatabase(options.Database);
    }

    public string Name => "Redis";

    public SyncDestinationCapabilities Capabilities =>
        SyncDestinationCapabilities.TransactionalBatches |
        SyncDestinationCapabilities.IdempotentUpserts |
        SyncDestinationCapabilities.Deletes |
        SyncDestinationCapabilities.CoLocatedCheckpoint;

    public async ValueTask<SyncProvisionResult> ProvisionAsync(
        SyncProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var keys = CreateKeys(request.PipelineId);
        var result = await _database.ScriptEvaluateAsync(
                ProvisionScript,
                [keys.State],
                [
                    CurrentFormatVersion,
                    request.PipelineId,
                    request.Source.Fingerprint,
                    request.Transform.Name,
                    request.Transform.Fingerprint,
                ])
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var values = (RedisResult[])result!;
        var status = (int)values[0];
        if (status == 1)
        {
            return new SyncProvisionResult(
                SyncProvisionStatus.RebuildRequired,
                (string)values[1]!);
        }

        if (status == 2)
        {
            throw new RedisSyncSourceMismatchException(
                $"Redis Sync pipeline '{request.PipelineId}' belongs to a different source.");
        }

        if (status == 3)
        {
            throw new RedisSyncException(
                $"Redis Sync storage format '{(string)values[1]!}' is unsupported; this build requires format {CurrentFormatVersion}.");
        }

        if (status == 4)
        {
            throw new RedisSyncException(
                $"Redis Sync pipeline hash collision detected for '{request.PipelineId}'.");
        }

        var runtime = new PipelineRuntime(
            request.PipelineId,
            request.Source,
            request.Transform,
            keys);
        _pipelines.AddOrUpdate(request.PipelineId, runtime, (_, _) => runtime);
        return new SyncProvisionResult(SyncProvisionStatus.Ready);
    }

    public async ValueTask ResetSnapshotAsync(
        string pipelineId,
        SnapshotReset reset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reset);
        var runtime = RequirePipeline(pipelineId, reset.Epoch.Source);
        await using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var registered = await _database.SetMembersAsync(runtime.Keys.Collections)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var keys = new RedisKey[registered.Length + 2];
        keys[0] = runtime.Keys.State;
        keys[1] = runtime.Keys.Collections;
        for (var index = 0; index < registered.Length; index++)
        {
            keys[index + 2] = (RedisKey)registered[index].ToString();
        }

        var result = await _database.ScriptEvaluateAsync(
                ResetSnapshotScript,
                keys,
                [runtime.Source.Fingerprint, runtime.Transform.Fingerprint, reset.Epoch.Value.ToString("N")])
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        EnsureOperationResult((int)result, runtime, reset.Epoch.Value);
    }

    public async ValueTask StartSnapshotAsync(
        string pipelineId,
        SnapshotStart start,
        SyncTransformVersion transform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        var runtime = RequirePipeline(pipelineId, start.Epoch.Source, transform);
        await ValidateSnapshotStateAsync(runtime, start.Epoch.Value, false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask ApplySnapshotBatchAsync(
        SyncSnapshotBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var runtime = RequirePipeline(
            batch.PipelineId,
            batch.SourceBatch.Epoch.Source,
            batch.Transform);
        ValidateLimits(
            batch.Mutations.Count,
            batch.Mutations.Select(mutation => mutation.Content));
        await using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var operations = batch.Mutations
            .Select((mutation, ordinal) => new MaterializedOperation(
                ordinal,
                SyncMutationKind.Upsert,
                mutation.Collection,
                mutation.Key,
                RedisSyncDocumentCodec.Encode(
                    StableSnapshotId(mutation.RowId),
                    mutation.Content,
                    mutation.ContentType,
                    mutation.PartitionKey)))
            .ToArray();
        var command = BuildSnapshotBatchCommand(runtime, batch.SourceBatch.Epoch.Value, operations);
        var result = await _database.ScriptEvaluateAsync(
                SnapshotBatchScript,
                command.Keys,
                command.Arguments)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        EnsureOperationResult((int)result, runtime, batch.SourceBatch.Epoch.Value);
    }

    public async ValueTask CompleteSnapshotAsync(
        string pipelineId,
        SnapshotComplete complete,
        SyncTransformVersion transform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(complete);
        var runtime = RequirePipeline(pipelineId, complete.Epoch.Source, transform);
        await ValidateSnapshotStateAsync(runtime, complete.Epoch.Value, true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<SyncApplyResult> ApplyTransactionAsync(
        SyncTransactionBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var runtime = RequirePipeline(batch.PipelineId, batch.Transaction.Source);
        if (!string.Equals(
                batch.Transform.Fingerprint,
                runtime.Transform.Fingerprint,
                StringComparison.Ordinal))
        {
            return new SyncApplyResult(
                SyncApplyStatus.TransformVersionMismatch,
                null,
                runtime.Transform.Fingerprint);
        }

        ValidateLimits(batch.Mutations.Count, batch.Mutations.Select(mutation => mutation.Content));
        var operations = PlanTransaction(batch);
        await using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var command = BuildTransactionCommand(runtime, batch, operations);
        var result = (int)await _database.ScriptEvaluateAsync(
                TransactionScript,
                command.Keys,
                command.Arguments)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            0 => SyncApplyResult.Applied(batch.Transaction.CommitEndPosition),
            1 => SyncApplyResult.AlreadyApplied(batch.Transaction.CommitEndPosition),
            2 => new SyncApplyResult(
                SyncApplyStatus.TransformVersionMismatch,
                null,
                runtime.Transform.Fingerprint),
            3 => throw new RedisSyncSourceMismatchException(
                $"Redis Sync pipeline '{batch.PipelineId}' belongs to a different source."),
            5 => throw new RedisSyncException(
                $"Redis Sync pipeline '{batch.PipelineId}' contains a key with an incompatible Redis type."),
            _ => throw new RedisSyncException($"Redis transaction script returned status {result}."),
        };
    }

    public async ValueTask<RedisSyncDocument?> ReadDocumentAsync(
        string pipelineId,
        string collection,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var runtime = RequirePipeline(pipelineId);
        var value = await _database.HashGetAsync(CollectionKey(runtime.Keys, collection), key)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return value.IsNull ? null : RedisSyncDocumentCodec.Decode((byte[])value!);
    }

    public async ValueTask<bool> StoreAsync(
        SyncQuarantineRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var runtime = RequirePipeline(record.PipelineId, record.Source, record.Transform);
        var field = Position(record.CommitEndPosition) + ":" +
            record.TransactionId.ToString("x8", CultureInfo.InvariantCulture);
        var value = Encoding.UTF8.GetBytes(string.Join(
            '\n',
            record.RecordedAt.ToString("O", CultureInfo.InvariantCulture),
            record.ErrorType,
            record.ErrorMessage,
            record.Transform.Fingerprint));
        _ = await _database.HashSetAsync(
                runtime.Keys.Quarantine,
                field,
                value,
                When.NotExists)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask ValidateSnapshotStateAsync(
        PipelineRuntime runtime,
        Guid epoch,
        bool complete,
        CancellationToken cancellationToken)
    {
        await using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var result = await _database.ScriptEvaluateAsync(
                SnapshotStateScript,
                [runtime.Keys.State],
                [
                    runtime.Source.Fingerprint,
                    runtime.Transform.Fingerprint,
                    epoch.ToString("N"),
                    complete ? "complete" : "validate",
                ])
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        EnsureOperationResult((int)result, runtime, epoch);
    }

    private static MaterializedOperation[] PlanTransaction(SyncTransactionBatch batch)
    {
        var lastCollectionDeletes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < batch.Mutations.Count; index++)
        {
            var mutation = batch.Mutations[index];
            if (mutation.Kind == SyncMutationKind.DeleteCollection)
            {
                lastCollectionDeletes[mutation.Collection] = index;
            }
        }

        var planned = new List<MaterializedOperation>();
        var finalKeys = new Dictionary<(string Collection, string Key), MaterializedOperation>();
        for (var index = 0; index < batch.Mutations.Count; index++)
        {
            var mutation = batch.Mutations[index];
            var lastDelete = lastCollectionDeletes.GetValueOrDefault(mutation.Collection, -1);
            if (index < lastDelete)
            {
                continue;
            }

            if (mutation.Kind == SyncMutationKind.DeleteCollection)
            {
                if (index == lastDelete)
                {
                    planned.Add(new MaterializedOperation(
                        index,
                        mutation.Kind,
                        mutation.Collection,
                        null,
                        []));
                }

                continue;
            }

            var value = mutation.Kind == SyncMutationKind.Upsert
                ? RedisSyncDocumentCodec.Encode(
                    StableChangeId(mutation.ChangeId),
                    mutation.Content,
                    mutation.ContentType!,
                    mutation.PartitionKey)
                : [];
            finalKeys[(mutation.Collection, mutation.Key!)] = new MaterializedOperation(
                index,
                mutation.Kind,
                mutation.Collection,
                mutation.Key,
                value);
        }

        planned.AddRange(finalKeys.Values);
        return planned.OrderBy(operation => operation.Ordinal).ToArray();
    }

    private RedisCommand BuildTransactionCommand(
        PipelineRuntime runtime,
        SyncTransactionBatch batch,
        IReadOnlyList<MaterializedOperation> operations)
    {
        var builder = new CommandBuilder(runtime, operations.Select(operation => operation.Collection));
        var arguments = new List<RedisValue>(5 + (operations.Count * 4))
        {
            runtime.Source.Fingerprint,
            runtime.Transform.Fingerprint,
            Position(batch.Transaction.CommitEndPosition),
            batch.Transaction.TransactionId.ToString(CultureInfo.InvariantCulture),
            operations.Count,
        };
        foreach (var operation in operations)
        {
            arguments.Add(builder.KeyIndex(operation.Collection));
            arguments.Add((int)operation.Kind);
            arguments.Add(operation.Key ?? string.Empty);
            arguments.Add(operation.Value);
        }

        ValidateEncodedSize(operations);
        return new RedisCommand(builder.Keys, arguments.ToArray());
    }

    private RedisCommand BuildSnapshotBatchCommand(
        PipelineRuntime runtime,
        Guid epoch,
        IReadOnlyList<MaterializedOperation> operations)
    {
        var builder = new CommandBuilder(runtime, operations.Select(operation => operation.Collection));
        var arguments = new List<RedisValue>(4 + (operations.Count * 3))
        {
            runtime.Source.Fingerprint,
            runtime.Transform.Fingerprint,
            epoch.ToString("N"),
            operations.Count,
        };
        foreach (var operation in operations)
        {
            arguments.Add(builder.KeyIndex(operation.Collection));
            arguments.Add(operation.Key!);
            arguments.Add(operation.Value);
        }

        ValidateEncodedSize(operations);
        return new RedisCommand(builder.Keys, arguments.ToArray());
    }

    private void ValidateLimits(int count, IEnumerable<ReadOnlyMemory<byte>> contents)
    {
        if (count > _options.MaxMutationsPerTransaction)
        {
            throw new RedisSyncException(
                $"The {count}-mutation batch exceeds the configured {_options.MaxMutationsPerTransaction}-mutation Redis limit.");
        }

        long total = 0;
        foreach (var content in contents)
        {
            if (content.Length > _options.MaxDocumentBytes)
            {
                throw new RedisSyncException(
                    $"A {content.Length}-byte document exceeds the configured {_options.MaxDocumentBytes}-byte Redis limit.");
            }

            total = checked(total + content.Length);
            if (total > _options.MaxTransactionBytes)
            {
                throw new RedisSyncException(
                    $"The transformed batch exceeds the configured {_options.MaxTransactionBytes}-byte Redis limit.");
            }
        }
    }

    private void ValidateEncodedSize(IEnumerable<MaterializedOperation> operations)
    {
        long size = 0;
        foreach (var operation in operations)
        {
            size = checked(size + operation.Value.Length);
            if (size > _options.MaxTransactionBytes)
            {
                throw new RedisSyncException(
                    $"The encoded batch exceeds the configured {_options.MaxTransactionBytes}-byte Redis limit.");
            }
        }
    }

    private static void EnsureOperationResult(int result, PipelineRuntime runtime, Guid epoch)
    {
        switch (result)
        {
            case 0:
                return;
            case 2:
                throw new RedisSyncException(
                    $"Redis Sync pipeline '{runtime.PipelineId}' changed transform metadata after provisioning.");
            case 3:
                throw new RedisSyncSourceMismatchException(
                    $"Redis Sync pipeline '{runtime.PipelineId}' belongs to a different source.");
            case 4:
                throw new RedisSyncSnapshotException(
                    $"Snapshot epoch '{epoch}' is not the active incomplete Redis destination epoch.");
            case 5:
                throw new RedisSyncException(
                    $"Redis Sync pipeline '{runtime.PipelineId}' contains a key with an incompatible Redis type.");
            default:
                throw new RedisSyncException($"Redis snapshot script returned status {result}.");
        }
    }

    private PipelineRuntime RequirePipeline(
        string pipelineId,
        ChangeSourceIdentity? source = null,
        SyncTransformVersion? transform = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        if (!_pipelines.TryGetValue(pipelineId, out var runtime))
        {
            throw new RedisSyncException(
                $"Redis Sync pipeline '{pipelineId}' must be provisioned successfully before use.");
        }

        if (source is not null &&
            !string.Equals(source.Fingerprint, runtime.Source.Fingerprint, StringComparison.Ordinal))
        {
            throw new RedisSyncSourceMismatchException(
                $"Redis Sync pipeline '{pipelineId}' belongs to source '{runtime.Source.Fingerprint}', not '{source.Fingerprint}'.");
        }

        if (transform is not null &&
            !string.Equals(
                transform.Fingerprint,
                runtime.Transform.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new SyncTransformVersionMismatchException(
                runtime.Transform.Fingerprint,
                transform.Fingerprint);
        }

        return runtime;
    }

    private PipelineKeys CreateKeys(string pipelineId)
    {
        var pipelineHash = Fingerprint(pipelineId);
        var root = _options.KeyPrefix + ":{" + pipelineHash + "}";
        return new PipelineKeys(
            root,
            root + ":state",
            root + ":collections",
            root + ":quarantine");
    }

    private static RedisKey CollectionKey(PipelineKeys keys, string collection) =>
        keys.Root + ":collection:" + Fingerprint(collection);

    private static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Position(BlueTuskLogSequenceNumber value) =>
        value.Value.ToString("x16", CultureInfo.InvariantCulture);

    private static string StableChangeId(ChangeId id) =>
        $"{id.Source.Fingerprint}:{id.CommitEndPosition.Value:x16}:{id.TransactionId:x8}:{id.Ordinal:x8}";

    private static string StableSnapshotId(SnapshotRowId id) =>
        $"{id.Epoch:N}:{id.TableIdentity}:{id.KeyIdentity}";

    private sealed class CommandBuilder
    {
        private readonly Dictionary<string, int> _indexes = new(StringComparer.Ordinal);

        internal CommandBuilder(PipelineRuntime runtime, IEnumerable<string> collections)
        {
            var keys = new List<RedisKey> { runtime.Keys.State, runtime.Keys.Collections };
            foreach (var collection in collections.Distinct(StringComparer.Ordinal))
            {
                _indexes[collection] = keys.Count + 1;
                keys.Add(CollectionKey(runtime.Keys, collection));
            }

            Keys = keys.ToArray();
        }

        internal RedisKey[] Keys { get; }

        internal int KeyIndex(string collection) => _indexes[collection];
    }

    private sealed class PipelineRuntime
    {
        private readonly Channel<bool> _gate = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });

        internal PipelineRuntime(
            string pipelineId,
            ChangeSourceIdentity source,
            SyncTransformVersion transform,
            PipelineKeys keys)
        {
            PipelineId = pipelineId;
            Source = source;
            Transform = transform;
            Keys = keys;
            if (!_gate.Writer.TryWrite(true))
            {
                throw new RedisSyncException("Unable to initialize the Redis pipeline operation gate.");
            }
        }

        internal string PipelineId { get; }

        internal ChangeSourceIdentity Source { get; }

        internal SyncTransformVersion Transform { get; }

        internal PipelineKeys Keys { get; }

        internal async ValueTask<GateLease> EnterAsync(CancellationToken cancellationToken)
        {
            _ = await _gate.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new GateLease(_gate.Writer);
        }
    }

    private sealed class GateLease(ChannelWriter<bool> writer) : IAsyncDisposable
    {
        private ChannelWriter<bool>? _writer = writer;

        public ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref _writer, null);
            if (current is not null && !current.TryWrite(true))
            {
                throw new RedisSyncException("Unable to release the Redis pipeline operation gate.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record PipelineKeys(
        string Root,
        RedisKey State,
        RedisKey Collections,
        RedisKey Quarantine);

    private sealed record MaterializedOperation(
        int Ordinal,
        SyncMutationKind Kind,
        string Collection,
        string? Key,
        byte[] Value);

    private sealed record RedisCommand(RedisKey[] Keys, RedisValue[] Arguments);
}

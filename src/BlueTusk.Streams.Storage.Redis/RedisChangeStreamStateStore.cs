using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.TypeSystem;
using StackExchange.Redis;

namespace BlueTusk.Streams.Storage.Redis;

public sealed record RedisChangeStreamStateStoreOptions
{
    public required IConnectionMultiplexer Connection { get; init; }

    public int Database { get; init; } = -1;

    public string KeyPrefix { get; init; } = "bluetusk:streams";

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(KeyPrefix);
        if (KeyPrefix.Contains('{') || KeyPrefix.Contains('}'))
        {
            throw new ArgumentException(
                "The Redis key prefix cannot contain cluster hash-tag braces.",
                nameof(KeyPrefix));
        }
    }
}

public sealed class RedisChangeStreamStateStore : IChangeStreamStateStore
{
    private const string AcquireScript = """
        local clock = redis.call('TIME')
        local now = (clock[1] * 1000) + math.floor(clock[2] / 1000)
        local owner = redis.call('HGET', KEYS[1], 'lease_owner')
        local token = redis.call('HGET', KEYS[1], 'lease_token')
        local expires = tonumber(redis.call('HGET', KEYS[1], 'lease_expires') or '0')
        if owner and expires > now and owner ~= ARGV[1] then
            return {0, owner, token, string.format('%.0f', expires)}
        end
        if not (owner and expires > now and owner == ARGV[1]) then
            token = redis.call('HINCRBY', KEYS[1], 'last_fencing_token', 1)
        end
        local new_expires = now + tonumber(ARGV[2])
        redis.call('HSET', KEYS[1],
            'lease_owner', ARGV[1],
            'lease_token', token,
            'lease_expires', string.format('%.0f', new_expires))
        return {1, ARGV[1], token, string.format('%.0f', new_expires)}
        """;

    private const string RenewScript = """
        local clock = redis.call('TIME')
        local now = (clock[1] * 1000) + math.floor(clock[2] / 1000)
        local owner = redis.call('HGET', KEYS[1], 'lease_owner')
        local token = redis.call('HGET', KEYS[1], 'lease_token')
        local expires = tonumber(redis.call('HGET', KEYS[1], 'lease_expires') or '0')
        if owner ~= ARGV[1] or token ~= ARGV[2] or expires <= now then
            return false
        end
        local new_expires = now + tonumber(ARGV[3])
        redis.call('HSET', KEYS[1], 'lease_expires', string.format('%.0f', new_expires))
        return string.format('%.0f', new_expires)
        """;

    private const string ReleaseScript = """
        local owner = redis.call('HGET', KEYS[1], 'lease_owner')
        local token = redis.call('HGET', KEYS[1], 'lease_token')
        if owner ~= ARGV[1] or token ~= ARGV[2] then
            return 0
        end
        redis.call('HDEL', KEYS[1], 'lease_owner', 'lease_token', 'lease_expires')
        return 1
        """;

    private const string CompareExchangeScript = """
        local clock = redis.call('TIME')
        local now = (clock[1] * 1000) + math.floor(clock[2] / 1000)
        local owner = redis.call('HGET', KEYS[1], 'lease_owner')
        local token = redis.call('HGET', KEYS[1], 'lease_token')
        local expires = tonumber(redis.call('HGET', KEYS[1], 'lease_expires') or '0')
        if owner ~= ARGV[1] or token ~= ARGV[2] or expires <= now then
            return 3
        end
        local generation = redis.call('HGET', KEYS[1], 'store_generation') or '-1'
        if generation ~= ARGV[3] then
            return 1
        end
        local stored_source = redis.call('HGET', KEYS[1], 'checkpoint_source')
        if stored_source and (
            stored_source ~= ARGV[14] or
            redis.call('HGET', KEYS[1], 'checkpoint_format') ~= ARGV[6] or
            redis.call('HGET', KEYS[1], 'system_identifier') ~= ARGV[7] or
            redis.call('HGET', KEYS[1], 'database_name') ~= ARGV[8] or
            redis.call('HGET', KEYS[1], 'slot_name') ~= ARGV[9] or
            redis.call('HGET', KEYS[1], 'publication_fingerprint') ~= ARGV[10] or
            redis.call('HGET', KEYS[1], 'database_identity') ~= ARGV[11] or
            redis.call('HGET', KEYS[1], 'output_plugin') ~= ARGV[12] or
            redis.call('HGET', KEYS[1], 'mapping_fingerprint') ~= ARGV[13]) then
            return 4
        end
        local current_position = redis.call('HGET', KEYS[1], 'acknowledged_position')
        if current_position and current_position > ARGV[5] then
            return 2
        end
        redis.call('HSET', KEYS[1],
            'store_generation', ARGV[4],
            'acknowledged_position', ARGV[5],
            'checkpoint_format', ARGV[6],
            'system_identifier', ARGV[7],
            'database_name', ARGV[8],
            'slot_name', ARGV[9],
            'publication_fingerprint', ARGV[10],
            'database_identity', ARGV[11],
            'output_plugin', ARGV[12],
            'mapping_fingerprint', ARGV[13],
            'checkpoint_source', ARGV[14])
        return 0
        """;

    private static readonly RedisValue[] CheckpointFields =
    [
        "checkpoint_format",
        "system_identifier",
        "database_name",
        "slot_name",
        "publication_fingerprint",
        "database_identity",
        "output_plugin",
        "mapping_fingerprint",
        "acknowledged_position",
        "store_generation",
    ];

    private readonly IDatabase _database;
    private readonly string _keyPrefix;

    public RedisChangeStreamStateStore(RedisChangeStreamStateStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _database = options.Connection.GetDatabase(options.Database);
        _keyPrefix = options.KeyPrefix;
    }

    public async ValueTask<ChangeStreamCheckpoint?> ReadAsync(
        ChangeStreamStateKey key,
        CancellationToken cancellationToken = default)
    {
        var values = await _database.HashGetAsync(GetKey(key), CheckpointFields)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return ReadCheckpoint(values);
    }

    public async ValueTask<ChangeCheckpointWriteResult> CompareExchangeAsync(
        ChangeStreamStateKey key,
        long expectedGeneration,
        ChangeStreamCheckpoint replacement,
        ChangeStreamLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(lease);
        ChangeCheckpointWriteStatus? precondition = null;
        if (replacement.StoreGeneration != checked(expectedGeneration + 1))
        {
            precondition = ChangeCheckpointWriteStatus.Conflict;
        }
        else if (!string.Equals(key.SourceFingerprint, replacement.Source.Fingerprint, StringComparison.Ordinal))
        {
            precondition = ChangeCheckpointWriteStatus.Incompatible;
        }

        if (precondition.HasValue)
        {
            return new ChangeCheckpointWriteResult(
                precondition.Value,
                await ReadAsync(key, cancellationToken).ConfigureAwait(false));
        }

        RedisValue[] arguments =
        [
            lease.OwnerId,
            lease.FencingToken.ToString(CultureInfo.InvariantCulture),
            expectedGeneration.ToString(CultureInfo.InvariantCulture),
            replacement.StoreGeneration.ToString(CultureInfo.InvariantCulture),
            FormatPosition(replacement.AcknowledgedCommitPosition),
            replacement.FormatVersion.ToString(CultureInfo.InvariantCulture),
            replacement.Source.SystemIdentifier,
            replacement.Source.DatabaseName,
            replacement.Source.SlotName,
            replacement.Source.PublicationFingerprint,
            replacement.DatabaseIdentity,
            replacement.OutputPlugin,
            replacement.MappingFingerprint,
            replacement.Source.Fingerprint,
        ];
        var result = await _database.ScriptEvaluateAsync(
                CompareExchangeScript,
                [GetKey(key)],
                arguments)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var status = (ChangeCheckpointWriteStatus)(int)result;
        if (status == ChangeCheckpointWriteStatus.Stored)
        {
            return new ChangeCheckpointWriteResult(status, replacement);
        }

        return new ChangeCheckpointWriteResult(
            status,
            await ReadAsync(key, cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<ChangeLeaseAcquireResult> AcquireAsync(
        ChangeStreamStateKey key,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseArguments(ownerId, duration);
        var result = await _database.ScriptEvaluateAsync(
                AcquireScript,
                [GetKey(key)],
                [ownerId, DurationMilliseconds(duration)])
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var values = (RedisResult[])result!;
        var lease = new ChangeStreamLease(
            key,
            (string)values[1]!,
            ParseInt64((string)values[2]!),
            ParseExpiration((string)values[3]!));
        return new ChangeLeaseAcquireResult(
            (int)values[0] == 1
                ? ChangeLeaseAcquireStatus.Acquired
                : ChangeLeaseAcquireStatus.HeldByAnotherOwner,
            lease);
    }

    public async ValueTask<ChangeStreamLease?> RenewAsync(
        ChangeStreamLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLeaseArguments(lease.OwnerId, duration);
        var result = await _database.ScriptEvaluateAsync(
                RenewScript,
                [GetKey(lease.Key)],
                [
                    lease.OwnerId,
                    lease.FencingToken.ToString(CultureInfo.InvariantCulture),
                    DurationMilliseconds(duration),
                ])
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return result.IsNull
            ? null
            : lease with { ExpiresAt = ParseExpiration((string)result!) };
    }

    public async ValueTask<bool> ReleaseAsync(
        ChangeStreamLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var result = await _database.ScriptEvaluateAsync(
                ReleaseScript,
                [GetKey(lease.Key)],
                [lease.OwnerId, lease.FencingToken.ToString(CultureInfo.InvariantCulture)])
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return (int)result == 1;
    }

    private RedisKey GetKey(ChangeStreamStateKey key)
    {
        var bytes = Encoding.UTF8.GetBytes(key.SourceFingerprint + "\n" + key.ConsumerGroup);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return $"{_keyPrefix}:{{{hash}}}";
    }

    private static ChangeStreamCheckpoint? ReadCheckpoint(RedisValue[] values)
    {
        if (values.Length != CheckpointFields.Length || values[0].IsNull)
        {
            return null;
        }

        return new ChangeStreamCheckpoint(
            ParseInt32(values[0]),
            new ChangeSourceIdentity(
                values[1]!,
                values[2]!,
                values[3]!,
                values[4]!),
            values[5]!,
            values[6]!,
            values[7]!,
            new BlueTuskLogSequenceNumber(ParseUInt64(values[8])),
            ParseInt64(values[9]));
    }

    private static RedisValue DurationMilliseconds(TimeSpan duration) =>
        checked((long)Math.Ceiling(duration.TotalMilliseconds));

    private static string FormatPosition(BlueTuskLogSequenceNumber position) =>
        position.Value.ToString("D20", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseExpiration(string value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ParseInt64(value));

    private static int ParseInt32(RedisValue value) =>
        int.Parse((string)value!, NumberStyles.None, CultureInfo.InvariantCulture);

    private static long ParseInt64(RedisValue value) =>
        long.Parse((string)value!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static ulong ParseUInt64(RedisValue value) =>
        ulong.Parse((string)value!, NumberStyles.None, CultureInfo.InvariantCulture);

    private static void ValidateLeaseArguments(string ownerId, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        if (duration.TotalMilliseconds > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
    }
}

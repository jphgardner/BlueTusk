using StackExchange.Redis;

namespace BlueTusk.Sync.Redis;

public sealed record RedisSyncOptions
{
    public required IConnectionMultiplexer Connection { get; init; }

    public int Database { get; init; } = -1;

    public string KeyPrefix { get; init; } = "bluetusk:sync";

    public int MaxDocumentBytes { get; init; } = 8 * 1024 * 1024;

    public long MaxTransactionBytes { get; init; } = 32L * 1024 * 1024;

    public int MaxMutationsPerTransaction { get; init; } = 10_000;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(KeyPrefix);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTransactionBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)MaxDocumentBytes, MaxTransactionBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMutationsPerTransaction);
        if (KeyPrefix.Contains('{') || KeyPrefix.Contains('}'))
        {
            throw new ArgumentException(
                "The Redis key prefix cannot contain cluster hash-tag braces.",
                nameof(KeyPrefix));
        }
    }
}

public class RedisSyncException : Exception
{
    public RedisSyncException(string message)
        : base(message)
    {
    }

    public RedisSyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class RedisSyncSourceMismatchException : RedisSyncException
{
    public RedisSyncSourceMismatchException(string message)
        : base(message)
    {
    }
}

public sealed class RedisSyncSnapshotException : RedisSyncException
{
    public RedisSyncSnapshotException(string message)
        : base(message)
    {
    }
}

public sealed class RedisSyncDocumentException : RedisSyncException
{
    public RedisSyncDocumentException(string message)
        : base(message)
    {
    }

    public RedisSyncDocumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

namespace BlueTusk.Replication;

/// <summary>A message received from a PostgreSQL WAL sender.</summary>
public abstract record BlueTuskReplicationMessage;

/// <summary>A contiguous section of physical or logical WAL output.</summary>
public sealed record BlueTuskXLogData(
    BlueTuskLogSequenceNumber WalStart,
    BlueTuskLogSequenceNumber ServerWalEnd,
    DateTimeOffset ServerClock,
    ReadOnlyMemory<byte> Data) : BlueTuskReplicationMessage
{
    /// <summary>The position immediately after this message's data.</summary>
    public BlueTuskLogSequenceNumber WalEnd =>
        WalStart + checked((ulong)Data.Length);
}

/// <summary>A keepalive sent by the PostgreSQL WAL sender.</summary>
public sealed record BlueTuskPrimaryKeepalive(
    BlueTuskLogSequenceNumber ServerWalEnd,
    DateTimeOffset ServerClock,
    bool ReplyRequested) : BlueTuskReplicationMessage;

/// <summary>The receiver positions sent to PostgreSQL as replication feedback.</summary>
public readonly record struct BlueTuskStandbyStatus(
    BlueTuskLogSequenceNumber Written,
    BlueTuskLogSequenceNumber Flushed,
    BlueTuskLogSequenceNumber Applied,
    bool ReplyRequested = false);

/// <summary>Transaction visibility feedback sent by a physical standby.</summary>
public readonly record struct BlueTuskHotStandbyFeedback(
    uint Xmin,
    uint XminEpoch,
    uint CatalogXmin,
    uint CatalogXminEpoch);

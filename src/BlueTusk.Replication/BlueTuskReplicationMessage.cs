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
    private bool _ownsData;

    /// <summary>The position immediately after this message's data.</summary>
    public BlueTuskLogSequenceNumber WalEnd =>
        WalStart + checked((ulong)Data.Length);

    internal bool OwnsData => _ownsData;

    internal BlueTuskXLogData MarkDataOwned()
    {
        _ownsData = true;
        return this;
    }

    public bool Equals(BlueTuskXLogData? other) =>
        ReferenceEquals(this, other) ||
        other is not null &&
        EqualityContract == other.EqualityContract &&
        WalStart.Equals(other.WalStart) &&
        ServerWalEnd.Equals(other.ServerWalEnd) &&
        ServerClock.Equals(other.ServerClock) &&
        Data.Equals(other.Data);

    public override int GetHashCode()
    {
        var hashCode = EqualityComparer<Type>.Default.GetHashCode(EqualityContract);
        hashCode = (hashCode * -1521134295) +
            EqualityComparer<BlueTuskLogSequenceNumber>.Default.GetHashCode(WalStart);
        hashCode = (hashCode * -1521134295) +
            EqualityComparer<BlueTuskLogSequenceNumber>.Default.GetHashCode(ServerWalEnd);
        hashCode = (hashCode * -1521134295) +
            EqualityComparer<DateTimeOffset>.Default.GetHashCode(ServerClock);
        return (hashCode * -1521134295) +
            EqualityComparer<ReadOnlyMemory<byte>>.Default.GetHashCode(Data);
    }
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

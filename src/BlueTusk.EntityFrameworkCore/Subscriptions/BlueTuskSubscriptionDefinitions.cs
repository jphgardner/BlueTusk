namespace BlueTusk.EntityFrameworkCore.Subscriptions;

public enum BlueTuskSubscriptionConnectionKind
{
    ConnectionString,
    ForeignServer,
    Redacted,
}

public sealed record BlueTuskSubscriptionConnection(
    BlueTuskSubscriptionConnectionKind Kind,
    string? Value)
{
    public static BlueTuskSubscriptionConnection FromConnectionString(string connectionString) =>
        new(BlueTuskSubscriptionConnectionKind.ConnectionString, connectionString);

    public static BlueTuskSubscriptionConnection FromForeignServer(string serverName) =>
        new(BlueTuskSubscriptionConnectionKind.ForeignServer, serverName);

    public static BlueTuskSubscriptionConnection Redacted { get; } =
        new(BlueTuskSubscriptionConnectionKind.Redacted, null);
}

public enum BlueTuskSubscriptionStreamingMode
{
    Off,
    On,
    Parallel,
}

public enum BlueTuskSubscriptionSynchronousCommit
{
    Off,
    Local,
    RemoteWrite,
    On,
    RemoteApply,
}

public enum BlueTuskSubscriptionOrigin
{
    Any,
    None,
}

public sealed record BlueTuskSubscriptionDefinition(
    string Name,
    BlueTuskSubscriptionConnection Connection,
    IReadOnlyList<string> Publications,
    string? SlotName,
    bool Enabled,
    bool Binary,
    BlueTuskSubscriptionStreamingMode Streaming,
    BlueTuskSubscriptionSynchronousCommit SynchronousCommit,
    bool TwoPhase,
    bool DisableOnError,
    bool PasswordRequired,
    bool RunAsOwner,
    BlueTuskSubscriptionOrigin Origin,
    bool Failover,
    bool RetainDeadTuples,
    int MaxRetentionDuration,
    string? WalReceiverTimeout,
    bool ConnectOnCreate,
    bool CreateSlot,
    bool CopyData);

public sealed record BlueTuskSubscriptionDefinitionSet(IReadOnlyList<BlueTuskSubscriptionDefinition> Subscriptions)
{
    public static BlueTuskSubscriptionDefinitionSet Empty { get; } = new([]);
}

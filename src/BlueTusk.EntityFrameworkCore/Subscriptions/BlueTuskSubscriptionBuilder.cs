namespace BlueTusk.EntityFrameworkCore.Subscriptions;

public sealed class BlueTuskSubscriptionBuilder
{
    private readonly List<string> _publications = [];

    internal BlueTuskSubscriptionBuilder(string name)
    {
        Name = name;
    }

    private string Name { get; }
    private BlueTuskSubscriptionConnection ConnectionValue { get; set; } = BlueTuskSubscriptionConnection.Redacted;
    private string? SlotNameValue { get; set; }
    private bool EnabledValue { get; set; }
    private bool BinaryValue { get; set; }
    private BlueTuskSubscriptionStreamingMode StreamingValue { get; set; }
    private BlueTuskSubscriptionSynchronousCommit SynchronousCommitValue { get; set; }
    private bool TwoPhaseValue { get; set; }
    private bool DisableOnErrorValue { get; set; }
    private bool PasswordRequiredValue { get; set; } = true;
    private bool RunAsOwnerValue { get; set; }
    private BlueTuskSubscriptionOrigin OriginValue { get; set; }
    private bool FailoverValue { get; set; }
    private bool RetainDeadTuplesValue { get; set; }
    private int MaxRetentionDurationValue { get; set; }
    private string? WalReceiverTimeoutValue { get; set; }
    private bool ConnectOnCreateValue { get; set; }
    private bool CreateSlotValue { get; set; }
    private bool CopyDataValue { get; set; }

    public BlueTuskSubscriptionBuilder UseConnectionString(string connectionString)
    {
        ConnectionValue = BlueTuskSubscriptionConnection.FromConnectionString(connectionString);
        return this;
    }

    public BlueTuskSubscriptionBuilder UseForeignServer(string serverName)
    {
        ConnectionValue = BlueTuskSubscriptionConnection.FromForeignServer(serverName);
        return this;
    }

    public BlueTuskSubscriptionBuilder HasRedactedConnection()
    {
        ConnectionValue = BlueTuskSubscriptionConnection.Redacted;
        return this;
    }

    public BlueTuskSubscriptionBuilder FromPublication(params string[] publicationNames)
    {
        _publications.AddRange(publicationNames);
        return this;
    }

    public BlueTuskSubscriptionBuilder UseSlot(string slotName)
    {
        SlotNameValue = slotName;
        return this;
    }

    public BlueTuskSubscriptionBuilder WithoutSlot()
    {
        SlotNameValue = null;
        return this;
    }

    public BlueTuskSubscriptionBuilder ConnectOnCreate(
        bool createSlot = true,
        bool copyData = true,
        bool enabled = true)
    {
        ConnectOnCreateValue = true;
        CreateSlotValue = createSlot;
        CopyDataValue = copyData;
        EnabledValue = enabled;
        if (createSlot && SlotNameValue is null)
        {
            SlotNameValue = Name;
        }

        return this;
    }

    public BlueTuskSubscriptionBuilder IsEnabled(bool enabled = true)
    {
        EnabledValue = enabled;
        return this;
    }

    public BlueTuskSubscriptionBuilder UsesBinary(bool enabled = true)
    {
        BinaryValue = enabled;
        return this;
    }

    public BlueTuskSubscriptionBuilder UsesStreaming(BlueTuskSubscriptionStreamingMode mode)
    {
        StreamingValue = mode;
        return this;
    }

    public BlueTuskSubscriptionBuilder UsesSynchronousCommit(BlueTuskSubscriptionSynchronousCommit mode)
    {
        SynchronousCommitValue = mode;
        return this;
    }

    public BlueTuskSubscriptionBuilder UsesTwoPhaseCommit(bool enabled = true)
    {
        TwoPhaseValue = enabled;
        return this;
    }

    public BlueTuskSubscriptionBuilder DisableOnError(bool enabled = true)
    {
        DisableOnErrorValue = enabled;
        return this;
    }

    public BlueTuskSubscriptionBuilder RequiresPassword(bool required = true)
    {
        PasswordRequiredValue = required;
        return this;
    }

    public BlueTuskSubscriptionBuilder RunAsOwner(bool enabled = true)
    {
        RunAsOwnerValue = enabled;
        return this;
    }

    public BlueTuskSubscriptionBuilder UsesOrigin(BlueTuskSubscriptionOrigin origin)
    {
        OriginValue = origin;
        return this;
    }

    public BlueTuskSubscriptionBuilder SupportsFailover(bool enabled = true)
    {
        FailoverValue = enabled;
        return this;
    }

    public BlueTuskSubscriptionBuilder RetainsDeadTuples(bool enabled = true, int maxRetentionDuration = 0)
    {
        RetainDeadTuplesValue = enabled;
        MaxRetentionDurationValue = maxRetentionDuration;
        return this;
    }

    public BlueTuskSubscriptionBuilder HasWalReceiverTimeout(string? timeout)
    {
        WalReceiverTimeoutValue = timeout;
        return this;
    }

    internal BlueTuskSubscriptionDefinition Build() => new(
        Name,
        ConnectionValue,
        _publications.ToArray(),
        SlotNameValue,
        EnabledValue,
        BinaryValue,
        StreamingValue,
        SynchronousCommitValue,
        TwoPhaseValue,
        DisableOnErrorValue,
        PasswordRequiredValue,
        RunAsOwnerValue,
        OriginValue,
        FailoverValue,
        RetainDeadTuplesValue,
        MaxRetentionDurationValue,
        WalReceiverTimeoutValue,
        ConnectOnCreateValue,
        CreateSlotValue,
        CopyDataValue);
}

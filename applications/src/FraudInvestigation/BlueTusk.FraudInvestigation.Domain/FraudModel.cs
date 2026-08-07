namespace BlueTusk.FraudInvestigation.Domain;

public sealed class Account
{
    private Account()
    {
    }

    private Account(Guid id, string tenantId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Id = id;
        TenantId = tenantId.Trim();
        DisplayName = displayName.Trim();
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public string TenantId { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Account Create(string tenantId, string displayName, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), tenantId, displayName);
}

public sealed class Transfer
{
    private Transfer()
    {
    }

    private Transfer(
        Guid id,
        string tenantId,
        Guid sourceId,
        Guid destinationId,
        decimal amount,
        string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        if (sourceId == destinationId)
        {
            throw new ArgumentException("Transfer endpoints must be different.", nameof(destinationId));
        }

        Id = id;
        TenantId = tenantId.Trim();
        SourceId = sourceId;
        DestinationId = destinationId;
        Amount = amount;
        Currency = currency.Trim().ToUpperInvariant();
        RecordedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public string TenantId { get; private set; } = string.Empty;

    public Guid SourceId { get; private set; }

    public Guid DestinationId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public DateTimeOffset RecordedAt { get; private set; }

    public static Transfer Record(
        string tenantId,
        Guid sourceId,
        Guid destinationId,
        decimal amount,
        string currency,
        Guid? id = null) =>
        new(id ?? Guid.NewGuid(), tenantId, sourceId, destinationId, amount, currency);
}

public sealed class AlertRule
{
    private AlertRule()
    {
    }

    public AlertRule(string tenantId, string name, decimal minimumAmount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumAmount);
        Id = Guid.NewGuid();
        TenantId = tenantId.Trim();
        Name = name.Trim();
        MinimumAmount = minimumAmount;
        Enabled = true;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public string TenantId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public decimal MinimumAmount { get; private set; }

    public bool Enabled { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

public enum CaseDecision
{
    Pending,
    Cleared,
    Suspicious,
    Escalated,
}

public sealed class InvestigationCase
{
    private InvestigationCase()
    {
    }

    public InvestigationCase(string tenantId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Id = Guid.NewGuid();
        TenantId = tenantId.Trim();
        Reason = reason.Trim();
        Decision = CaseDecision.Pending;
        OpenedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public string TenantId { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public string? Assignee { get; private set; }

    public CaseDecision Decision { get; private set; }

    public string? DecisionNote { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public void Assign(string assignee, long expectedVersion)
    {
        ExpectVersion(expectedVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(assignee);
        Assignee = assignee.Trim();
        Version++;
    }

    public void Decide(CaseDecision decision, string note, long expectedVersion)
    {
        ExpectVersion(expectedVersion);
        if (decision == CaseDecision.Pending)
        {
            throw new ArgumentException("A final case decision cannot be Pending.", nameof(decision));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        Decision = decision;
        DecisionNote = note.Trim();
        DecidedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    private void ExpectVersion(long expectedVersion)
    {
        if (Version != expectedVersion)
        {
            throw new InvalidOperationException(
                $"Expected case version {expectedVersion}, but found {Version}.");
        }
    }
}

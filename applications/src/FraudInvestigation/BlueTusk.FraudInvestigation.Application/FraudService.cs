using BlueTusk.FraudInvestigation.Domain;

namespace BlueTusk.FraudInvestigation.Application;

public interface IFraudRepository
{
    ValueTask<Account?> FindAccountAsync(string tenantId, Guid id, CancellationToken cancellationToken);

    ValueTask AddAccountAsync(Account account, CancellationToken cancellationToken);

    ValueTask AddTransferAsync(Transfer transfer, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Account>> ListAccountsAsync(
        string tenantId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Transfer>> ListTransfersAsync(
        string tenantId,
        CancellationToken cancellationToken);

    ValueTask AddAlertRuleAsync(AlertRule rule, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<AlertRule>> ListAlertRulesAsync(
        string tenantId,
        CancellationToken cancellationToken);

    ValueTask AddCaseAsync(InvestigationCase investigationCase, CancellationToken cancellationToken);

    ValueTask<InvestigationCase?> FindCaseAsync(
        string tenantId,
        Guid id,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<InvestigationCase>> ListCasesAsync(
        string tenantId,
        CancellationToken cancellationToken);

    ValueTask AppendEvidenceAsync(FraudEvidenceEntry entry, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<FraudEvidenceEntry>> ListEvidenceAsync(
        string tenantId,
        Guid caseId,
        CancellationToken cancellationToken);

    ValueTask SaveAsync(CancellationToken cancellationToken);
}

public sealed record FraudEvidenceEntry(
    string TenantId,
    Guid CaseId,
    string Operation,
    string Actor,
    string Detail,
    DateTimeOffset RecordedAt);

public sealed record SuspiciousPath(
    IReadOnlyList<Guid> AccountIds,
    IReadOnlyList<Guid> TransferIds,
    decimal TotalAmount);

public sealed class FraudService(IFraudRepository repository)
{
    public async ValueTask<Account> RegisterAccountAsync(
        string tenantId,
        string displayName,
        CancellationToken cancellationToken)
    {
        var account = Account.Create(tenantId, displayName);
        await repository.AddAccountAsync(account, cancellationToken).ConfigureAwait(false);
        await repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        return account;
    }

    public async ValueTask<Transfer> RecordTransferAsync(
        string tenantId,
        Guid sourceId,
        Guid destinationId,
        decimal amount,
        string currency,
        CancellationToken cancellationToken)
    {
        _ = await repository.FindAccountAsync(tenantId, sourceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Source account was not found.");
        _ = await repository.FindAccountAsync(tenantId, destinationId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Destination account was not found.");
        var transfer = Transfer.Record(tenantId, sourceId, destinationId, amount, currency);
        await repository.AddTransferAsync(transfer, cancellationToken).ConfigureAwait(false);
        await repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        return transfer;
    }

    public async ValueTask<AlertRule> CreateAlertRuleAsync(
        string tenantId,
        string name,
        decimal minimumAmount,
        CancellationToken cancellationToken)
    {
        var rule = new AlertRule(tenantId, name, minimumAmount);
        await repository.AddAlertRuleAsync(rule, cancellationToken).ConfigureAwait(false);
        await repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        return rule;
    }

    public async ValueTask<InvestigationCase> OpenCaseAsync(
        string tenantId,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        var investigationCase = new InvestigationCase(tenantId, reason);
        await repository.AddCaseAsync(investigationCase, cancellationToken).ConfigureAwait(false);
        await repository.AppendEvidenceAsync(
            Evidence(tenantId, investigationCase.Id, "case.opened", actor, reason),
            cancellationToken).ConfigureAwait(false);
        await repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        return investigationCase;
    }

    public async ValueTask<InvestigationCase> AssignCaseAsync(
        string tenantId,
        Guid caseId,
        string assignee,
        string actor,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var investigationCase = await repository.FindCaseAsync(tenantId, caseId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Investigation case was not found.");
        investigationCase.Assign(assignee, expectedVersion);
        await repository.AppendEvidenceAsync(
            Evidence(tenantId, caseId, "case.assigned", actor, assignee),
            cancellationToken).ConfigureAwait(false);
        await repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        return investigationCase;
    }

    public async ValueTask<InvestigationCase> DecideCaseAsync(
        string tenantId,
        Guid caseId,
        CaseDecision decision,
        string note,
        string actor,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var investigationCase = await repository.FindCaseAsync(tenantId, caseId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Investigation case was not found.");
        investigationCase.Decide(decision, note, expectedVersion);
        await repository.AppendEvidenceAsync(
            Evidence(tenantId, caseId, "case.decided", actor, $"{decision}: {note}"),
            cancellationToken).ConfigureAwait(false);
        await repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        return investigationCase;
    }

    public async ValueTask<IReadOnlyList<SuspiciousPath>> FindSuspiciousPathsAsync(
        string tenantId,
        Guid accountId,
        int maximumHops,
        decimal minimumTotal,
        CancellationToken cancellationToken)
    {
        if (maximumHops is < 2 or > 6) { throw new ArgumentOutOfRangeException(nameof(maximumHops)); }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumTotal);
        _ = await repository.FindAccountAsync(tenantId, accountId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Account was not found.");
        var transfers = await repository.ListTransfersAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);
        var queue = new Queue<PathState>();
        queue.Enqueue(new PathState([accountId], [], 0m));
        var results = new List<SuspiciousPath>();
        while (queue.TryDequeue(out var path))
        {
            if (path.TransferIds.Count >= maximumHops) { continue; }
            var current = path.AccountIds[^1];
            foreach (var transfer in transfers.Where(item => item.SourceId == current))
            {
                if (path.AccountIds.Contains(transfer.DestinationId)) { continue; }
                var next = new PathState(
                    [.. path.AccountIds, transfer.DestinationId],
                    [.. path.TransferIds, transfer.Id],
                    path.TotalAmount + transfer.Amount);
                if (next.TransferIds.Count >= 2 && next.TotalAmount >= minimumTotal)
                {
                    results.Add(new SuspiciousPath(next.AccountIds, next.TransferIds, next.TotalAmount));
                }
                queue.Enqueue(next);
            }
        }
        return results.OrderByDescending(path => path.TotalAmount).Take(100).ToArray();
    }

    private static FraudEvidenceEntry Evidence(
        string tenantId,
        Guid caseId,
        string operation,
        string actor,
        string detail) =>
        new(tenantId, caseId, operation, actor, detail, DateTimeOffset.UtcNow);

    private sealed record PathState(
        IReadOnlyList<Guid> AccountIds,
        IReadOnlyList<Guid> TransferIds,
        decimal TotalAmount);
}

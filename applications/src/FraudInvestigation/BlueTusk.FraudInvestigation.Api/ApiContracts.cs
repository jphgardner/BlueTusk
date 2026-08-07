using BlueTusk.FraudInvestigation.Domain;

namespace BlueTusk.FraudInvestigation.Api;

public sealed record RegisterAccountRequest(string DisplayName);

public sealed record RecordTransferRequest(
    Guid SourceId,
    Guid DestinationId,
    decimal Amount,
    string Currency);

public sealed record OpenCaseRequest(string Reason);

public sealed record CreateAlertRuleRequest(string Name, decimal MinimumAmount);

public sealed record AssignCaseRequest(string Assignee, long ExpectedVersion);

public sealed record DecideCaseRequest(
    CaseDecision Decision,
    string Note,
    long ExpectedVersion);

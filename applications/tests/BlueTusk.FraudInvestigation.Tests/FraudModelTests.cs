using BlueTusk.FraudInvestigation.Domain;
using Xunit;

namespace BlueTusk.FraudInvestigation.Tests;

public sealed class FraudModelTests
{
    [Fact]
    public void Transfer_requires_distinct_accounts_and_positive_amount()
    {
        var account = Guid.NewGuid();
        _ = Assert.Throws<ArgumentException>(() =>
            Transfer.Record("tenant-a", account, account, 10, "GBP"));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Transfer.Record("tenant-a", Guid.NewGuid(), Guid.NewGuid(), 0, "GBP"));
    }

    [Fact]
    public void Case_decision_is_a_versioned_immutable_audit_fact()
    {
        var investigationCase = new InvestigationCase("tenant-a", "multi-hop velocity");

        investigationCase.Assign("analyst@example.test", 0);
        investigationCase.Decide(CaseDecision.Suspicious, "Confirmed network", 1);

        Assert.Equal(CaseDecision.Suspicious, investigationCase.Decision);
        Assert.Equal(2, investigationCase.Version);
        Assert.NotNull(investigationCase.DecidedAt);
        _ = Assert.Throws<InvalidOperationException>(() =>
            investigationCase.Decide(CaseDecision.Cleared, "stale", 1));
    }

    [Fact]
    public void Alert_rule_requires_a_name_and_positive_threshold()
    {
        var rule = new AlertRule("tenant-a", "High value path", 10_000m);

        Assert.True(rule.Enabled);
        Assert.Equal(10_000m, rule.MinimumAmount);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AlertRule("tenant-a", "Invalid", 0m));
        _ = Assert.Throws<ArgumentException>(() =>
            new AlertRule("tenant-a", " ", 10m));
    }
}

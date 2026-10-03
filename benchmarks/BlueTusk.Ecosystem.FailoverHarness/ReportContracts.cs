using System.Diagnostics;
using System.Text.Json.Serialization;

namespace BlueTusk.Ecosystem.FailoverHarness;

/// <summary>
/// One disturbance and its recovery. Every listed check ran and passed; a failed check stops the
/// harness with a non-zero exit before any report is written. The offline verifier compares the
/// check names against the family policy so a scenario cannot silently drop an assertion.
/// </summary>
internal sealed record ScenarioResult(
    string Name,
    string Fault,
    string DeliverySemantics,
    int Tenants,
    int AcknowledgedOperations,
    int VerifiedOperations,
    long ExpectedEffects,
    long ObservedEffects,
    bool InFlightAtomic,
    bool InFlightCommitted,
    bool NoCrossTenantReads,
    bool StaleOwnerRejected,
    long BeforeFence,
    long AfterFence,
    string BeforeSystemIdentifier,
    string AfterSystemIdentifier,
    int BeforeTimeline,
    int AfterTimeline,
    double FaultToFirstSuccessMilliseconds,
    double FaultToVerifiedMilliseconds,
    IReadOnlyList<string> Checks);

internal sealed record FamilyReport(
    int FormatVersion,
    string Family,
    string Fixture,
    string Server,
    string Image,
    string SourceDurability,
    IReadOnlyList<ScenarioResult> Scenarios,
    bool ProductionQualified,
    string Qualification);

/// <summary>Collects the passed checks and measurements for one scenario.</summary>
internal sealed class ScenarioRecorder(string name, string fault, string semantics, int tenants)
{
    private readonly List<string> _checks = [];
    private long _fault;

    internal string Name { get; } = name;
    internal int Acknowledged { get; set; }
    internal int Verified { get; set; }
    internal long ExpectedEffects { get; set; }
    internal long ObservedEffects { get; set; }
    internal bool InFlightAtomic { get; set; }
    internal bool InFlightCommitted { get; set; }
    internal bool NoCrossTenantReads { get; set; }
    internal bool StaleOwnerRejected { get; set; }
    internal long BeforeFence { get; set; }
    internal long AfterFence { get; set; }
    internal ServerIdentity? Before { get; set; }
    internal ServerIdentity? After { get; set; }
    internal double FirstSuccessMilliseconds { get; set; }
    internal long FaultTimestamp => _fault;

    internal void MarkFault() => _fault = Stopwatch.GetTimestamp();

    internal void Check(bool valid, string check)
    {
        FailoverFixture.Check(valid, Name + ": " + check);
        if (!_checks.Contains(check, StringComparer.Ordinal)) { _checks.Add(check); }
    }

    internal ScenarioResult Complete()
    {
        FailoverFixture.Check(_fault != 0 && Before is not null && After is not null && FirstSuccessMilliseconds > 0, Name + ": complete measurements");
        return new(Name, fault, semantics, tenants, Acknowledged, Verified, ExpectedEffects, ObservedEffects, InFlightAtomic,
            InFlightCommitted, NoCrossTenantReads, StaleOwnerRejected, BeforeFence, AfterFence, Before!.SystemIdentifier,
            After!.SystemIdentifier, Before.Timeline, After.Timeline, FirstSuccessMilliseconds,
            Stopwatch.GetElapsedTime(_fault).TotalMilliseconds, _checks.ToArray());
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FamilyReport))]
internal sealed partial class ReportJson : JsonSerializerContext;

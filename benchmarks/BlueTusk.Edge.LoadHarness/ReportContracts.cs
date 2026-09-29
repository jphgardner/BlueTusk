using System.Collections.Concurrent;

namespace BlueTusk.Edge.LoadHarness;

internal sealed record LatencySummary(long Samples, double P50Milliseconds, double P95Milliseconds,
    double P99Milliseconds, double MaximumMilliseconds);
internal sealed record PhysicalSample(double ElapsedSeconds, long OwnedRelationBytes, long DatabaseBytes,
    long WalInsertBytes, long ReceiptCount, long ReceiptBytes, long ChangeCount, long ChangeBytes);
internal sealed record ClientReport(int Index, string Kind, string OrderedStreamId, long Offered, long Skipped, long Acknowledged,
    long ExpectedConflicts, long UnexpectedConflicts, long PeakPending, long PeakOutbox,
    long FinalPending, long FinalOutbox, long FinalLocalReceipts, long FinalCheckpoint,
    long FinalOrderedSequence, long FinalHorizon, long MaximumPhysicalBytes,
    LatencySummary Enqueue, LatencySummary HttpApply, LatencySummary DurableAck, LatencySummary Horizon,
    double[] FaultRecoverySeconds, bool LostResponseRecovered, bool ReclaimedRetryFenced,
    bool ExactFinalCache);
internal sealed record CapacityReport(int FormatVersion, string CandidateSha, string SourceTreeSha256,
    string HarnessBinarySha256, string BrowserBundleSha256, string PostgreSqlImage, string PostgreSqlVersion,
    string Workload, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, int DurationSeconds,
    double MeasuredSeconds, double DrainSeconds, int SeededRecords, ClientReport[] Clients,
    long BusinessEffects, bool ExactBusinessEffects, bool ExactFeedState, bool HostRestarted,
    bool LaggingReaderResnapshotted, PhysicalSample[] StorageSamples, PhysicalSample AfterDrain,
    bool ProductionQualified, bool Passed);

internal sealed class LatencyCapture
{
    private readonly ConcurrentQueue<double> _values = new();
    private int _count;

    internal void Add(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0 || Interlocked.Increment(ref _count) > 100_000)
        { throw new InvalidOperationException("Latency sample bound or value is invalid."); }
        _values.Enqueue(milliseconds);
    }

    internal LatencySummary Snapshot()
    {
        var sorted = _values.ToArray();
        if (sorted.Length != _count || sorted.Length == 0) { throw new InvalidOperationException("Latency samples are missing."); }
        Array.Sort(sorted);
        static double At(double[] values, double fraction) => values[Math.Max(0, (int)Math.Ceiling(values.Length * fraction) - 1)];
        return new(sorted.LongLength, At(sorted, .5), At(sorted, .95), At(sorted, .99), sorted[^1]);
    }
}

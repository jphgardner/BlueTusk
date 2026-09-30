using System.Diagnostics.Metrics;

namespace BlueTusk.Events;

internal static class EventsDiagnostics
{
    private static readonly Meter Meter = new("BlueTusk.Events");
    private static readonly Counter<long> Appends = Meter.CreateCounter<long>("bluetusk.events.append.prepared", "{event}");
    private static readonly Counter<long> DuplicateAppends = Meter.CreateCounter<long>("bluetusk.events.append.duplicates", "{event}");
    private static readonly Counter<long> Inbox = Meter.CreateCounter<long>("bluetusk.events.inbox.attempts", "{event}");
    private static readonly Counter<long> Replay = Meter.CreateCounter<long>("bluetusk.events.replay.committed", "{event}");
    private static readonly Histogram<long> ReplayLag = Meter.CreateHistogram<long>("bluetusk.events.replay.remaining", "{event}");
    private static readonly Counter<long> Fenced = Meter.CreateCounter<long>("bluetusk.events.replay.fenced", "{attempt}");
    private static readonly Histogram<long> ObservedReplayLag = Meter.CreateHistogram<long>("bluetusk.events.replay.observed_remaining", "{event}");

    internal static void Prepared(int appended, int duplicates) { Add(Appends, appended); Add(DuplicateAppends, duplicates); }
    internal static void InboxAttempt(string outcome) => Add(Inbox, 1, outcome);
    internal static void ReplayCommitted(int count, long remaining)
    {
        Add(Replay, count);
        try { ReplayLag.Record(remaining); } catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
    internal static void LeaseFenced() => Add(Fenced, 1);
    internal static void ReplayObserved(long remaining)
    {
        if (!ObservedReplayLag.Enabled) { return; }
        try { ObservedReplayLag.Record(remaining); } catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private static void Add(Counter<long> counter, long value, string? outcome = null)
    {
        if (!counter.Enabled) { return; }
        try { counter.Add(value, new KeyValuePair<string, object?>("outcome", outcome)); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
}

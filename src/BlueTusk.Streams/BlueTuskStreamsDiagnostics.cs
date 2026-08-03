using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BlueTusk.Streams;

public static class BlueTuskStreamsDiagnostics
{
    public const string InstrumentationName = "BlueTusk.Streams";

    public static ActivitySource ActivitySource { get; } = new(InstrumentationName);

    public static Meter Meter { get; } = new(InstrumentationName);

    private static readonly Counter<long> TransactionsDelivered =
        Meter.CreateCounter<long>("bluetusk.streams.transactions.delivered", "{transaction}");
    private static readonly Counter<long> ChangesDelivered =
        Meter.CreateCounter<long>("bluetusk.streams.changes.delivered", "{change}");
    private static readonly Counter<long> SnapshotRowsDelivered =
        Meter.CreateCounter<long>("bluetusk.streams.snapshot.rows", "{row}");
    private static readonly Histogram<double> TransactionBytes =
        Meter.CreateHistogram<double>("bluetusk.streams.transaction.bytes", "By");

    internal static void RecordTransaction(ChangeTransaction transaction)
    {
        var tags = new TagList
        {
            { "bluetusk.source", transaction.Source.Fingerprint },
            { "bluetusk.slot", transaction.Source.SlotName },
        };
        TransactionsDelivered.Add(1, tags);
        ChangesDelivered.Add(transaction.Changes.Count, tags);
        TransactionBytes.Record(transaction.Changes.EstimatedBytes, tags);
    }

    internal static void RecordSnapshotBatch(ChangeSnapshotBatch batch)
    {
        var tags = new TagList
        {
            { "bluetusk.source", batch.Epoch.Source.Fingerprint },
            { "bluetusk.table", batch.Table.ToString() },
        };
        SnapshotRowsDelivered.Add(batch.Rows.Count, tags);
    }
}

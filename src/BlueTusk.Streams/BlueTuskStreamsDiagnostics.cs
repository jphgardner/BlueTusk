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
    private static readonly Counter<long> SpooledTransactions =
        Meter.CreateCounter<long>("bluetusk.streams.transactions.spooled", "{transaction}");
    private static readonly UpDownCounter<long> ActiveDeliveries =
        Meter.CreateUpDownCounter<long>("bluetusk.streams.deliveries.active", "{delivery}");
    private static readonly Counter<long> DeliverySettlements =
        Meter.CreateCounter<long>("bluetusk.streams.deliveries.settled", "{delivery}");
    private static readonly Counter<long> DeliverySettlementFailures =
        Meter.CreateCounter<long>("bluetusk.streams.delivery.settlement.failures", "{failure}");
    private static readonly Histogram<double> DeliveryDuration =
        Meter.CreateHistogram<double>("bluetusk.streams.delivery.duration", "s");

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
        if (transaction.Changes.IsSpooled)
        {
            SpooledTransactions.Add(1, tags);
        }
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

    internal static long StartDelivery(ChangeTransaction transaction)
    {
        if (ActiveDeliveries.Enabled)
        {
            ActiveDeliveries.Add(1, DeliveryTags(transaction, outcome: null));
        }

        return DeliveryDuration.Enabled
            ? Stopwatch.GetTimestamp()
            : 0;
    }

    internal static void RecordDeliverySettlement(
        ChangeTransaction transaction,
        string outcome,
        long started)
    {
        var tags = DeliveryTags(transaction, outcome);
        if (ActiveDeliveries.Enabled)
        {
            ActiveDeliveries.Add(-1, DeliveryTags(transaction, outcome: null));
        }

        if (DeliverySettlements.Enabled)
        {
            DeliverySettlements.Add(1, tags);
        }

        if (started != 0 && DeliveryDuration.Enabled)
        {
            DeliveryDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                tags);
        }
    }

    internal static void RecordDeliverySettlementFailure(
        ChangeTransaction transaction,
        string operation)
    {
        if (DeliverySettlementFailures.Enabled)
        {
            var tags = DeliveryTags(transaction, outcome: null);
            tags.Add("bluetusk.streams.delivery.operation", operation);
            DeliverySettlementFailures.Add(
                1,
                tags);
        }
    }

    private static TagList DeliveryTags(
        ChangeTransaction transaction,
        string? outcome) =>
        outcome is null
            ? new TagList
            {
                { "bluetusk.source", transaction.Source.Fingerprint },
                { "bluetusk.streams.spooled", transaction.Changes.IsSpooled },
            }
            : new TagList
            {
                { "bluetusk.source", transaction.Source.Fingerprint },
                { "bluetusk.streams.spooled", transaction.Changes.IsSpooled },
                { "bluetusk.streams.delivery.outcome", outcome },
            };
}

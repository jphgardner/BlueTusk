using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Tests;

public sealed class ProductionTelemetryTests
{
    [Fact]
    public async Task Delivery_lifecycle_emits_balanced_and_actionable_metrics()
    {
        var measurements = new ConcurrentQueue<Measurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "BlueTusk.Streams")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new Measurement(
                instrument.Name,
                value,
                ReadTag(tags, "bluetusk.streams.delivery.outcome"))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue(new Measurement(
                instrument.Name,
                value,
                ReadTag(tags, "bluetusk.streams.delivery.outcome"))));
        listener.Start();

        var source = new ChangeSourceIdentity(
            "telemetry",
            "database",
            "slot",
            "publication");
        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            source,
            transactionId: 1,
            new BlueTuskLogSequenceNumber(10));

        await delivery.AcknowledgeAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.streams.deliveries.active" &&
                item.Value == 1);
        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.streams.deliveries.active" &&
                item.Value == -1);
        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.streams.deliveries.settled" &&
                item.Value == 1 &&
                item.Outcome == "acknowledged");
        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.streams.delivery.duration" &&
                item.Value >= 0 &&
                item.Outcome == "acknowledged");
    }

    [Fact]
    public async Task Failed_settlement_records_the_operation_and_remains_active_for_retry()
    {
        var measurements = new ConcurrentQueue<Measurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "BlueTusk.Streams")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new Measurement(
                instrument.Name,
                value,
                ReadTag(tags, "bluetusk.streams.delivery.outcome"),
                ReadTag(tags, "bluetusk.streams.delivery.operation"))));
        listener.Start();

        var source = new ChangeSourceIdentity(
            "telemetry",
            "database",
            "slot",
            "publication");
        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            source,
            transactionId: 2,
            new BlueTuskLogSequenceNumber(20),
            observer: new FailingAcknowledgeObserver());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await delivery.AcknowledgeAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(ChangeDeliveryState.Active, delivery.State);

        await delivery.NackAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.streams.delivery.settlement.failures" &&
                item.Value == 1 &&
                item.Operation == "acknowledge");
        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.streams.deliveries.active" &&
                item.Value == -1);
    }

    private static string? ReadTag(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string name)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == name)
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    private sealed record Measurement(
        string Name,
        double Value,
        string? Outcome,
        string? Operation = null);

    private sealed class FailingAcknowledgeObserver : IChangeDeliveryObserver
    {
        public ValueTask AcknowledgeAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(
                new InvalidOperationException("Injected acknowledgement failure."));

        public ValueTask NackAsync(
            ChangeTransaction transaction,
            Exception? failure,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}

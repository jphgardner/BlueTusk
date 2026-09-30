namespace BlueTusk.Events.Tests;

public sealed class EventsMetricsTests
{
    [Fact]
    public async Task FaultyMetricObserverCannotUndoCallerOwnedAppendOrOwnedReplayCommit()
    {
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = static (instrument, l) => { if (instrument.Meter.Name == "BlueTusk.Events") { l.EnableMeasurementEvents(instrument); } }
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => throw new InvalidOperationException("Faulty telemetry observer."));
        listener.Start();
        await using var db = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        var value = new EventWrite(Guid.NewGuid(), "test.event", 1, DateTimeOffset.UtcNow, "payload"u8);
        await db.AppendAsync(stream, [value]);
        await db.AppendAsync(stream, [value]);
        var lease = Assert.IsType<EventReplayLease>(await db.Store.AcquireReplayAsync("consumer", stream, "worker", TimeSpan.FromMinutes(1)));
        Assert.Equal(1, (await db.Store.ReplayAsync(lease, db.HandleAsync)).HandledCount);
        Assert.Equal(1, await db.EffectCountAsync());
        Assert.Equal(1, await db.Store.ReadCheckpointAsync("consumer", stream));
    }
}

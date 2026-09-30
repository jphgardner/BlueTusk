using System.Diagnostics.Metrics;

namespace BlueTusk.Workflows.LoadHarness;

internal sealed class InstrumentProbe : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, InstrumentObservation> _observations = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    internal InstrumentProbe()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name is "BlueTusk.Jobs" or "BlueTusk.Workflows")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) => Record(instrument.Name, measurement));
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) => Record(instrument.Name, measurement));
        _listener.Start();
    }

    private void Record(string name, double value)
    {
        lock (_gate)
        {
            if (_observations.TryGetValue(name, out var previous))
            {
                _observations[name] = previous with { Measurements = previous.Measurements + 1, Sum = previous.Sum + value, Maximum = Math.Max(previous.Maximum, value) };
            }
            else
            {
                Program.Check(_observations.Count < 64, "bounded metric names");
                _observations.Add(name, new(name, 1, value, value));
            }
        }
    }

    internal IReadOnlyList<InstrumentObservation> Snapshot()
    {
        lock (_gate)
        {
            return _observations.Values.OrderBy(observation => observation.Name, StringComparer.Ordinal).ToArray();
        }
    }

    public void Dispose() => _listener.Dispose();
}

using System.Diagnostics;

namespace BlueTusk.Documents.LoadHarness;

// Fixed memory and no raw operation/payload retention: 128 subdivisions per power of two microseconds.
internal sealed class LatencyHistogram
{
    private readonly long[] _bins = new long[8192];
    private readonly object _gate = new();
    private long _count;
    private double _minimum = double.MaxValue;
    private double _maximum;
    internal void Add(long started)
    {
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var microseconds = Math.Max(1, (long)Math.Ceiling(milliseconds * 1000));
        var exponent = System.Numerics.BitOperations.Log2((ulong)microseconds);
        var baseValue = 1L << exponent;
        var subdivision = (int)((microseconds - baseValue) * 128 / baseValue);
        lock (_gate) { _bins[checked((int)exponent * 128 + subdivision)]++; _count++; _minimum = Math.Min(_minimum, milliseconds); _maximum = Math.Max(_maximum, milliseconds); }
    }
    internal Percentiles Snapshot()
    {
        lock (_gate) { return new(_count, _count == 0 ? 0 : _minimum, Quantile(.5), Quantile(.95), Quantile(.99), _maximum); }
    }
    private double Quantile(double fraction)
    {
        var wanted = (long)Math.Ceiling(_count * fraction); long cumulative = 0;
        if (wanted == 0) { return 0; }
        for (var index = 0; index < _bins.Length; index++)
        {
            cumulative += _bins[index];
            if (cumulative >= wanted) { return Math.Pow(2, index / 128) * (1 + ((index % 128) + 1) / 128.0) / 1000; }
        }
        return _maximum;
    }
}

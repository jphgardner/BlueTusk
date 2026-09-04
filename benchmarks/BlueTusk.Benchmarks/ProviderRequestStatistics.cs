namespace BlueTusk.Benchmarks;

/// <summary>Trial-level statistics; requests within one process are not independent trials.</summary>
internal static class ProviderRequestStatistics
{
    internal sealed record Metric(double CandidateTrialMean, double ReferenceTrialMean,
        double? GeometricPairedRatio, double? RatioCiLower, double? RatioCiUpper, string ConfidenceStatus);

    // NIST's 0.975 Student-t column, degrees of freedom 1..49. Add 0.001 to
    // each printed value so rounding to three decimals cannot narrow the interval.
    // https://www.itl.nist.gov/div898/handbook/eda/section3/eda3672.htm
    private static readonly double[] CriticalValues =
    [
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262,
        2.228, 2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101,
        2.093, 2.086, 2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052,
        2.048, 2.045, 2.042, 2.040, 2.037, 2.035, 2.032, 2.030, 2.028,
        2.026, 2.024, 2.023, 2.021, 2.020, 2.018, 2.017, 2.015, 2.014,
        2.013, 2.012, 2.011, 2.010,
    ];

    internal static Dictionary<string, double> Derive(ProviderRequestCapture.Window window, long frequency)
    {
        if (frequency <= 0 || window.ElapsedTicks <= 0 || window.CompletedOperations is < 1 or > 32_000_000 ||
            window.AllocatedBytes < 0 || !double.IsFinite(window.CpuMilliseconds) || window.CpuMilliseconds < 0 ||
            window.PeakWorkingSetBytes <= 0 || window.GcCollections is not { Length: 3 } ||
            window.GcCollections.Any(value => value < 0) || window.Workers is not { Length: >= 1 and <= 256 })
        {
            throw new InvalidDataException("Invalid raw measurement counters or sample bounds.");
        }
        long count = 0;
        foreach (var worker in window.Workers)
        {
            if (worker.Count <= 0 || worker.RequestTicks is null || worker.RequestTicks.Length != worker.Count ||
                worker.RequestTicks.Any(ticks => ticks <= 0 || ticks > window.ElapsedTicks))
            {
                throw new InvalidDataException("Raw request samples are missing, unresolvable, or outside their measurement window.");
            }
            count += worker.Count;
        }
        if (count != window.CompletedOperations)
        {
            throw new InvalidDataException("Raw sample count does not match completed operations.");
        }
        var ordered = new long[checked((int)count)];
        var offset = 0;
        foreach (var worker in window.Workers)
        {
            worker.RequestTicks.CopyTo(ordered, offset);
            offset += worker.Count;
        }
        Array.Sort(ordered);
        var microsecondsPerTick = 1_000_000d / frequency;
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["meanUs"] = ordered.Average(ticks => (double)ticks) * microsecondsPerTick,
            ["p95Us"] = Percentile(ordered, 0.95) * microsecondsPerTick,
            ["p99Us"] = Percentile(ordered, 0.99) * microsecondsPerTick,
            ["allocatedBytesPerOperation"] = (double)window.AllocatedBytes / count,
            ["operationsPerSecond"] = count / ((double)window.ElapsedTicks / frequency),
            ["cpuUsPerOperation"] = window.CpuMilliseconds * 1000 / count,
            ["peakRssBytes"] = window.PeakWorkingSetBytes,
        };
        if (metrics.Values.Any(value => !double.IsFinite(value) || value < 0))
        {
            throw new InvalidDataException("Derived metrics must be finite and nonnegative.");
        }
        return metrics;
    }

    internal static long Percentile(long[] sorted, double probability)
    {
        if (sorted.Length == 0 || !double.IsFinite(probability) || probability is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(probability));
        }
        return sorted[checked((int)Math.Ceiling(probability * sorted.Length)) - 1];
    }

    internal static Metric Compare(double[] candidate, double[] reference)
    {
        if (candidate.Length is < 1 or > 50 || candidate.Length != reference.Length ||
            candidate.Concat(reference).Any(value => !double.IsFinite(value) || value < 0))
        {
            throw new InvalidDataException("Paired metrics need 1..50 matched, finite, nonnegative trials.");
        }
        var candidateMean = candidate.Average();
        var referenceMean = reference.Average();
        if (candidate.Concat(reference).Any(value => value == 0))
        {
            return new(candidateMean, referenceMean, null, null, null, "nonpositive-metric; log-ratio inference unavailable");
        }
        var logs = candidate.Zip(reference, (a, b) => Math.Log(a) - Math.Log(b)).ToArray();
        var logMean = logs.Average();
        var ratio = Math.Exp(logMean);
        if (!double.IsFinite(ratio) || ratio <= 0)
        {
            throw new InvalidDataException("The paired ratio is outside finite numeric range.");
        }
        if (logs.Length < 5)
        {
            return new(candidateMean, referenceMean, ratio, null, null, "insufficient-trials; at least five pairs required");
        }
        var variance = logs.Sum(value => (value - logMean) * (value - logMean)) / (logs.Length - 1);
        var radius = (CriticalValues[logs.Length - 2] + 0.001) * Math.Sqrt(variance / logs.Length);
        var lower = Math.Exp(logMean - radius);
        var upper = Math.Exp(logMean + radius);
        if (!double.IsFinite(lower) || lower <= 0 || !double.IsFinite(upper))
        {
            return new(candidateMean, referenceMean, ratio, null, null, "interval-outside-numeric-range");
        }
        return new(candidateMean, referenceMean, ratio, lower, upper,
            "approximate-95%-paired-log-t; independent approximately normal trial log-ratios assumed");
    }

    internal static bool MeetsLowerTarget(Metric metric, double maximum) =>
        metric.GeometricPairedRatio is { } ratio && ratio <= maximum &&
        metric.RatioCiUpper is { } upper && upper <= maximum;
}

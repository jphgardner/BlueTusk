using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Benchmarks;

/// <summary>
/// Deterministic, seeded, studentized bootstrap of per-trial means.
/// One independently restarted process is one trial; requests inside a process are never
/// treated as independent observations. The method is specified in
/// docs/operations/core-performance-evidence.md so an independent checker can reproduce it.
/// </summary>
internal static class PerformanceEvidenceStatistics
{
    internal const string Method = "conservative-studentized-bootstrap-of-trial-means/2";
    internal const int Resamples = 10_000;
    internal const double ConfidenceLevel = 0.95;
    internal const double IntervalConfidenceLevel = 0.99;
    internal const string SeedPrefix = "bluetusk-performance-bootstrap/v2|";
    internal const double TailProbability = 0.005;
    internal const int MinimumQualificationTrials = 30;
    internal const int MinimumDiagnosticTrials = 3;
    internal const int MaximumTrials = 50;

    internal sealed record Interval(double Point, double Lower, double Upper);

    /// <summary>The seed is bound to the measured commit so it cannot be shopped for after seeing data.</summary>
    internal static string DeriveSeed(string sourceCommit) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SeedPrefix + sourceCommit)));

    /// <summary>
    /// Resampling plan: index[b, j] = UInt64LE(SHA-256(seed32 || Int32LE(n) || Int32LE(b) || Int32LE(j))[0..8]) mod n.
    /// </summary>
    internal static int[] CreatePlan(string seedHex, int trials, int resamples = Resamples)
    {
        var seed = Convert.FromHexString(seedHex);
        if (seed.Length != 32 || trials is < 2 or > MaximumTrials || resamples is < 1_000 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(trials), "The bootstrap plan needs a 32-byte seed, 2..50 trials and a bounded resample count.");
        }
        var plan = new int[checked(resamples * trials)];
        Span<byte> message = stackalloc byte[44];
        Span<byte> digest = stackalloc byte[32];
        seed.CopyTo(message);
        BinaryPrimitives.WriteInt32LittleEndian(message[32..], trials);
        for (var resample = 0; resample < resamples; resample++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(message[36..], resample);
            for (var draw = 0; draw < trials; draw++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(message[40..], draw);
                SHA256.HashData(message, digest);
                plan[resample * trials + draw] = (int)(BinaryPrimitives.ReadUInt64LittleEndian(digest) % (ulong)trials);
            }
        }
        return plan;
    }

    internal static (int Lower, int Upper) PercentileIndices(int resamples)
    {
        var lower = (int)Math.Floor(resamples * TailProbability);
        var upper = (int)Math.Ceiling(resamples * (1 - TailProbability)) - 1;
        return (Math.Clamp(lower, 0, resamples - 1), Math.Clamp(upper, 0, resamples - 1));
    }

    internal static Interval Estimate(IReadOnlyList<double> values, int[] plan, int resamples = Resamples)
    {
        var trials = values.Count;
        if (trials is < 2 or > MaximumTrials || plan.Length != trials * resamples ||
            values.Any(value => !double.IsFinite(value) || value < 0) || plan.Any(index => index < 0 || index >= trials))
        {
            throw new InvalidDataException("Bootstrap inputs must be 2..50 finite, nonnegative trial values with a matching plan.");
        }
        var sum = 0d;
        for (var index = 0; index < trials; index++)
        {
            sum += values[index];
        }
        var point = sum / trials;
        if (!double.IsFinite(point)) throw new InvalidDataException("Trial mean overflowed.");
        var standardError = StandardError(values, point);
        if (standardError == 0) return new(point, point, point);
        var pivots = new double[resamples];
        var sample = new double[trials];
        for (var resample = 0; resample < resamples; resample++)
        {
            var total = 0d;
            var offset = resample * trials;
            for (var draw = 0; draw < trials; draw++)
            {
                sample[draw] = values[plan[offset + draw]];
                total += sample[draw];
            }
            var mean = total / trials;
            var error = StandardError(sample, mean);
            // Keep singular draws in their correct tail, rather than discard them or substitute an SE.
            pivots[resample] = error == 0
                ? mean == point ? 0 : Math.CopySign(double.PositiveInfinity, mean - point)
                : (mean - point) / error;
        }
        Array.Sort(pivots);
        var (lowerIndex, upperIndex) = PercentileIndices(resamples);
        var lower = point - pivots[upperIndex] * standardError;
        var upper = point - pivots[lowerIndex] * standardError;
        if (!double.IsFinite(lower) || !double.IsFinite(upper))
        {
            throw new InvalidDataException("Studentized interval is unbounded; retain the capture and collect more independent trials.");
        }
        // Always bracket the point estimate; the verifier rejects bounds that do not.
        // Every reported metric is nonnegative, so intersect the interval with its parameter space.
        return new(point, Math.Max(0, Math.Min(lower, point)), Math.Max(upper, point));
    }

    private static double StandardError(IReadOnlyList<double> values, double mean)
    {
        var squared = 0d;
        foreach (var value in values) squared += (value - mean) * (value - mean);
        var error = Math.Sqrt(squared / (values.Count * (values.Count - 1.0)));
        if (!double.IsFinite(error)) throw new InvalidDataException("Trial standard error overflowed.");
        return error;
    }

    internal static double StudentTCdf(double t, int degreesOfFreedom)
    {
        if (degreesOfFreedom < 1 || !double.IsFinite(t))
        {
            throw new ArgumentOutOfRangeException(nameof(degreesOfFreedom));
        }
        var x = degreesOfFreedom / (degreesOfFreedom + t * t);
        var tail = 0.5 * RegularizedIncompleteBeta(degreesOfFreedom / 2.0, 0.5, x);
        return t >= 0 ? 1 - tail : tail;
    }

    internal static double StudentTQuantile(double probability, int degreesOfFreedom)
    {
        if (probability is <= 0.5 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(probability));
        }
        double low = 0, high = 1_000;
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var middle = (low + high) / 2;
            if (StudentTCdf(middle, degreesOfFreedom) < probability) low = middle;
            else high = middle;
        }
        return (low + high) / 2;
    }

    internal static double NormalCdf(double x) => x < 0
        ? 0.5 * RegularizedUpperGamma(0.5, x * x / 2)
        : 1 - 0.5 * RegularizedUpperGamma(0.5, x * x / 2);

    private static readonly double[] LanczosCoefficients =
    [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
        -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
        1.5056327351493116e-7,
    ];

    private static double LogGamma(double x)
    {
        if (x < 0.5)
        {
            return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);
        }
        x -= 1;
        var sum = LanczosCoefficients[0];
        for (var index = 1; index < LanczosCoefficients.Length; index++)
        {
            sum += LanczosCoefficients[index] / (x + index);
        }
        var t = x + 7.5;
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(sum);
    }

    private static double RegularizedIncompleteBeta(double a, double b, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        var front = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x));
        return x < (a + 1) / (a + b + 2)
            ? front * BetaContinuedFraction(a, b, x) / a
            : 1 - front * BetaContinuedFraction(b, a, 1 - x) / b;
    }

    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const double Tiny = 1e-300;
        double sum = a + b, plus = a + 1, minus = a - 1, c = 1, d = 1 - sum * x / plus;
        d = 1 / (Math.Abs(d) < Tiny ? Tiny : d);
        var h = d;
        for (var m = 1; m <= 10_000; m++)
        {
            var m2 = 2 * m;
            var term = m * (b - m) * x / ((minus + m2) * (a + m2));
            d = 1 + term * d;
            d = 1 / (Math.Abs(d) < Tiny ? Tiny : d);
            c = 1 + term / c;
            c = Math.Abs(c) < Tiny ? Tiny : c;
            h *= d * c;
            term = -(a + m) * (sum + m) * x / ((a + m2) * (plus + m2));
            d = 1 + term * d;
            d = 1 / (Math.Abs(d) < Tiny ? Tiny : d);
            c = 1 + term / c;
            c = Math.Abs(c) < Tiny ? Tiny : c;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < 1e-16) return h;
        }
        throw new ArithmeticException("Incomplete beta continued fraction did not converge.");
    }

    private static double RegularizedUpperGamma(double a, double x)
    {
        if (x <= 0) return 1;
        var logFront = -x + a * Math.Log(x) - LogGamma(a);
        if (x < a + 1)
        {
            double term = 1 / a, sum = term, denominator = a;
            for (var index = 0; index < 10_000; index++)
            {
                denominator += 1;
                term *= x / denominator;
                sum += term;
                if (Math.Abs(term) < Math.Abs(sum) * 1e-17) return 1 - sum * Math.Exp(logFront);
            }
            throw new ArithmeticException("Incomplete gamma series did not converge.");
        }
        const double Tiny = 1e-300;
        double b = x + 1 - a, c = 1 / Tiny, d = 1 / b, h = d;
        for (var index = 1; index <= 10_000; index++)
        {
            var an = -index * (index - a);
            b += 2;
            d = an * d + b;
            d = 1 / (Math.Abs(d) < Tiny ? Tiny : d);
            c = b + an / c;
            c = Math.Abs(c) < Tiny ? Tiny : c;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < 1e-16) return Math.Exp(logFront) * h;
        }
        throw new ArithmeticException("Incomplete gamma continued fraction did not converge.");
    }
}

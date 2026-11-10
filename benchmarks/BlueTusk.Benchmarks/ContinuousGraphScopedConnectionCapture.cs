using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BlueTusk.ContinuousGraph;
using BlueTusk.Data;

namespace BlueTusk.Benchmarks;

public partial class ContinuousGraphBenchmarks
{
    internal static async Task CaptureScopedConnectionAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var samples = new List<ScopedConnectionSample>();
        foreach (var top in new[] { 10, 1_000 })
        {
            foreach (var restrictedRole in new[] { false, true })
            {
                await using var benchmark = new ContinuousGraphBenchmarks { EdgeCount = 1_000, TopN = top };
                await benchmark.SetupAsync(observer: null, restrictedRole, compareScopedConnection: true);
                var authorized = benchmark._connectionScoped
                    ?? throw new InvalidOperationException("The connection-scoped session is missing.");
                var leased = benchmark._leaseScoped
                    ?? throw new InvalidOperationException("The lease-scoped session is missing.");
                (string Name, ContinuousGraphMaintenanceTier Tier, Func<Task<ContinuousGraphMaintenanceTier>> Evaluate)[] cases =
                [
                    ("scoped-ef", ContinuousGraphMaintenanceTier.AuthoritativeDelta, benchmark.ScopedUpdateAsync),
                    ("scoped-connection", ContinuousGraphMaintenanceTier.AuthoritativeDelta,
                        () => benchmark.EvaluateUpdateAsync(authorized)),
                    ("scoped-lease", ContinuousGraphMaintenanceTier.AuthoritativeDelta,
                        () => benchmark.EvaluateUpdateAsync(leased)),
                    ("full-repair", ContinuousGraphMaintenanceTier.AuthoritativeRepair, benchmark.FullRepairUpdateAsync),
                ];
                const int warmup = 64;
                const int iterationsPerRound = 256;
                const int rounds = 3;
                var profile = restrictedRole ? "restricted-role" : "qualification-fixture";
                for (var sample = 0; sample < warmup + iterationsPerRound * rounds; sample++)
                {
                    // Interleave the same mutation and full-repair workload under one authorization profile.
                    for (var offset = 0; offset < cases.Length; offset++)
                    {
                        var workload = cases[(sample + offset) % cases.Length];
                        var before = GC.GetTotalAllocatedBytes(precise: true);
                        var started = Stopwatch.GetTimestamp();
                        var tier = await workload.Evaluate();
                        var microseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                        if (tier != workload.Tier)
                        {
                            throw new InvalidOperationException($"The {workload.Name} workload used an unexpected tier.");
                        }

                        if (sample >= warmup)
                        {
                            var measured = sample - warmup;
                            samples.Add(new(top, profile, measured / iterationsPerRound,
                                measured % iterationsPerRound, workload.Name, microseconds, allocated));
                        }
                    }
                }

                var expectedAuthorizations = 1 + warmup + iterationsPerRound * rounds;
                if (benchmark._scopeSelections != expectedAuthorizations || benchmark._leaseSelections != expectedAuthorizations)
                {
                    throw new InvalidOperationException("Every opt-in probe must invoke its own subscriber authorization.");
                }

                await using var prepared = new BlueTuskCommand(
                    "SELECT count(*) FROM pg_prepared_statements WHERE statement LIKE 'SELECT g.%LIMIT 2'",
                    benchmark._authorizedConnection
                        ?? throw new InvalidOperationException("The authorized connection is missing."));
                if (!Equals(await prepared.ExecuteScalarAsync(), 1L))
                {
                    throw new InvalidOperationException("PostgreSQL did not prepare the authorized graph probe.");
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "round-trip-samples.json"), JsonSerializer.Serialize(new
        {
            os = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            arch = RuntimeInformation.ProcessArchitecture.ToString(),
            samples,
        }));
    }

    private sealed record ScopedConnectionSample(
        int Top, string Profile, int Round, int Sample, string Arm, double Microseconds, long AllocatedBytes);
}

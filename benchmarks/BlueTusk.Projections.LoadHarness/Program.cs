using System.Globalization;
using System.Text.Json;

namespace BlueTusk.Projections.LoadHarness;

internal static class Program
{
    internal static void Check(bool condition, string invariant)
    {
        if (!condition) { throw new InvalidOperationException("Invariant failed: " + invariant); }
    }

    private static async Task<int> Main(string[] args)
    {
        var profile = args.Length == 0 ? "quick" : args[0];
        if (profile == "micro")
        {
            var summaries = BenchmarkDotNet.Running.BenchmarkSwitcher.FromTypes([typeof(EventAdmissionBenchmarks)]).Run(args[1..]).ToArray();
            return summaries.Length > 0 && summaries.All(static summary => !summary.HasCriticalValidationErrors &&
                summary.Reports.Length > 0 && summary.Reports.All(static report => report.Success)) ? 0 : 1;
        }
        if (profile is not ("quick" or "matrix" or "soak" or "capacity" or "promotion")) { throw new ArgumentException("Profile must be quick, matrix, soak, capacity or promotion."); }
        var seconds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : profile is "soak" or "capacity" ? 600 : 5;
        ArgumentOutOfRangeException.ThrowIfLessThan(seconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds, 3600);
        var output = Path.GetFullPath(args.Length > 2 ? args[2] : "artifacts/projections-load/report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var connection = Environment.GetEnvironmentVariable("BLUETUSK_PROJECTIONS_LOAD_CONNECTION_STRING")
            ?? throw new InvalidOperationException("A dedicated disposable fixture connection string is required.");
        var smokePayload = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 128;
        if (smokePayload is not (128 or 4096 or 65536)) { throw new ArgumentOutOfRangeException(nameof(args), "Smoke payload must be 128, 4096 or 65536."); }
        var cases = profile switch
        {
            "matrix" => Matrix(seconds),
            "soak" => [new LoadCase("overload-fairness", seconds, 4096, 32, 16, 10_000, 64, 1500, 6, 256, 150_000, 600)],
            "capacity" => [new LoadCase("sustainable-capacity", seconds, 4096, 32, 16, 0, 64, 20, 6, 256, 150_000, 120)],
            "promotion" => [new LoadCase("physical-backlog", seconds, 4096, 8, 4, 2_000, 16, 400, 12, 128, 30_000, 240)],
            _ => [new LoadCase("smoke", seconds, smokePayload, 2, 2, 100, 4, 100, 12, 64, 10_000, 90)]
        };
        var reports = new List<ScenarioReport>();
        var started = DateTimeOffset.UtcNow;
        try
        {
            foreach (var value in cases)
            {
                Console.WriteLine($"Starting {value.Name}: tenants={value.Tenants}, payload={value.PayloadBytes}, writers={value.Writers}, backlog={value.Backlog}, hot fanout={value.Fanout}.");
                reports.Add(await Scenario.RunAsync(connection, value, profile == "promotion", output));
                await SaveAsync(output, new(started, DateTimeOffset.UtcNow, profile, Environment.Version.ToString(),
                    Environment.OSVersion.ToString(), false, true, reports.ToArray(), null));
                Console.WriteLine($"Passed {value.Name}: committed={reports[^1].Committed}, rejected={reports[^1].Rejected}, elapsed={reports[^1].MeasuredSeconds:F2}s.");
            }
            return 0;
        }
        catch (Exception exception)
        {
            // Never serialize exception SQL, connection strings or payloads into campaign artifacts.
            await SaveAsync(output, new(started, DateTimeOffset.UtcNow, profile, Environment.Version.ToString(),
                Environment.OSVersion.ToString(), false, false, reports.ToArray(), exception.GetType().Name));
            Console.Error.WriteLine("Campaign failed: " + exception.GetType().Name);
            Console.Error.WriteLine(exception.StackTrace);
            if (exception is ArgumentException admission) { Console.Error.WriteLine("Admission parameter: " + admission.ParamName); }
            Console.Error.WriteLine(exception.Message.Contains("Invariant failed:", StringComparison.Ordinal) ? exception.Message : "Inspect the owned fixture and diagnostic debugger; sensitive database exception details are omitted.");
            return 1;
        }
    }

    private static LoadCase[] Matrix(int seconds) =>
    [
        new("payload-small", seconds, 128, 1, 1, 0, 1, 100, 12, 128, 150_000, 600),
        new("payload-medium", seconds, 4096, 32, 4, 0, 1, 400, 12, 128, 150_000, 600),
        new("payload-large", seconds, 65536, 256, 16, 0, 1, 400, 24, 128, 150_000, 600),
        new("writers-one", seconds, 4096, 256, 1, 0, 1, 100, 12, 128, 150_000, 600),
        new("writers-four", seconds, 128, 32, 4, 0, 64, 400, 12, 128, 150_000, 600),
        new("writers-sixteen", seconds, 128, 1, 16, 0, 1, 1000, 24, 256, 150_000, 600),
        new("backlog-ten-thousand", seconds, 128, 32, 4, 10_000, 1, 200, 12, 128, 150_000, 600),
        new("backlog-hundred-thousand", seconds, 128, 256, 16, 100_000, 1, 200, 24, 128, 150_000, 1200),
        new("fanout-sixty-four", seconds, 4096, 32, 4, 0, 64, 100, 12, 128, 150_000, 600),
        new("fanout-five-twelve", seconds, 128, 32, 4, 0, 512, 100, 12, 128, 150_000, 600),
        new("hot-cold-fairness", seconds, 4096, 32, 16, 0, 64, 1500, 6, 256, 150_000, 600),
        new("large-payload-backlog", seconds, 65536, 1, 4, 10_000, 1, 200, 8, 128, 150_000, 900)
    ];

    private static async Task SaveAsync(string path, CampaignReport report) =>
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(report, ReportJson.Default.CampaignReport));
}

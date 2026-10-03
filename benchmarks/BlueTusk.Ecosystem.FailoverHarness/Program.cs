using System.Diagnostics;
using System.Text.Json;

namespace BlueTusk.Ecosystem.FailoverHarness;

internal static class Program
{
    private const string Qualification = "Owned local PostgreSQL 18 synchronous primary/physical-standby fixture on one host. Backend termination, host-process kill, primary crash/restart and primary loss with standby promotion; not asynchronous-loss, split-brain, old-primary rejoin, storage-loss, independent-host or production availability qualification.";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--child", var childFamily, var role, .. var rest])
            {
                using var childDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await RunChildAsync(childFamily, role, rest, childDeadline.Token);
                return 0;
            }

            if (args is not [var family, var reportPath])
            {
                await Console.Error.WriteLineAsync("Usage: <family> <report-path> (fixture variables are supplied by eng/run-expansion-failover.ps1).");
                return 2;
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            await using var fixture = new FailoverFixture();
            await fixture.RequireSynchronousAsync(TimeSpan.FromSeconds(30), deadline.Token);
            var identity = await fixture.IdentifyAsync(deadline.Token);
            IReadOnlyList<ScenarioResult> scenarios = family switch
            {
                "Documents" => await DocumentsFailover.RunAsync(fixture, deadline.Token),
                "Projections" => await ProjectionsFailover.RunAsync(fixture, deadline.Token),
                _ => throw new InvalidOperationException("Unknown failover family."),
            };
            var report = new FamilyReport(1, family, FailoverFixture.Fixture, identity.Version, FailoverFixture.Image,
                "fsync=on and synchronous_commit=remote_apply to one verified synchronous physical standby before every disturbance; the promoted survivor keeps local synchronous commit",
                scenarios, false, Qualification);
            string output = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, JsonSerializer.SerializeToUtf8Bytes(report, ReportJson.Default.FamilyReport), deadline.Token);
            Console.WriteLine(family + " failover scenarios verified; raw report written.");
            return 0;
        }
        catch (Exception exception)
        {
            // Exception messages are harness-authored contracts; provider messages are not echoed.
            string message = exception is HarnessCheckException ? exception.Message : exception.GetType().FullName ?? "unknown";
            await Console.Error.WriteLineAsync("Failover harness stopped: " + message);
            if (Environment.GetEnvironmentVariable("BLUETUSK_HARNESS_DIAGNOSTICS") == "1") { await Console.Error.WriteLineAsync(exception.ToString()); }
            return 1;
        }
    }

    private static Task RunChildAsync(string family, string role, string[] arguments, CancellationToken token) => family switch
    {
        "Documents" => DocumentsFailover.RunChildAsync(role, arguments, token),
        "Projections" => ProjectionsFailover.RunChildAsync(role, arguments, token),
        _ => throw new InvalidOperationException("Unknown failover child family."),
    };
}

/// <summary>A real operating-system child process running product code; the parent kills its whole tree.</summary>
internal sealed class ChildProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _errors;

    private ChildProcess(Process process)
    {
        _process = process;
        _errors = process.StandardError.ReadToEndAsync();
    }

    internal static ChildProcess Start(string family, string role, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--child");
        start.ArgumentList.Add(family);
        start.ArgumentList.Add(role);
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        return new(Process.Start(start) ?? throw new InvalidOperationException("Owned failover child did not start."));
    }

    internal int ProcessId => _process.Id;

    internal async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken token)
    {
        string? line = await _process.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(timeout, token);
        FailoverFixture.Check(line is not null, "the child reported progress before exiting");
        return line!;
    }

    internal async Task ExpectAsync(string expected, TimeSpan timeout, CancellationToken token) =>
        FailoverFixture.Check(await ReadLineAsync(timeout, token) == expected, "the child reported '" + expected + "'");

    /// <summary>Hard-kills the child and its tree; no shutdown hook, finally block or dispose runs.</summary>
    internal async Task KillAsync(CancellationToken token)
    {
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(token);
        FailoverFixture.Check(_process.HasExited, "the child process is dead");
        _ = await _errors;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        try { _ = await _errors.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        _process.Dispose();
    }
}

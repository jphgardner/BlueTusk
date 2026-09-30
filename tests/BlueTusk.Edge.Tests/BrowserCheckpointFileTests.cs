using System.Diagnostics;
using BlueTusk.Edge.LoadHarness;

namespace BlueTusk.Edge.Tests;

public sealed class BrowserCheckpointFileTests
{
    [Fact]
    public async Task Node_publishes_complete_checkpoint_after_observer_releases_previous_snapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bluetusk-edge-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "browser.checkpoint");
            await File.WriteAllTextAsync(path, "1234567890123456789");
            using var process = new Process();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Task<string> errors;
            using (var previous = BrowserCheckpointFile.OpenSnapshot(path))
            {
                var start = new ProcessStartInfo("node")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add("--input-type=module");
                start.ArgumentList.Add("--eval");
                start.ArgumentList.Add("const { publishCheckpoint } = await import(process.argv[1]); await publishCheckpoint(process.argv[2], '987654321');");
                start.ArgumentList.Add(new Uri(Path.Combine(AppContext.BaseDirectory, "checkpoint.mjs")).AbsoluteUri);
                start.ArgumentList.Add(path);
                process.StartInfo = start;
                Assert.True(process.Start());
                errors = process.StandardError.ReadToEndAsync(timeout.Token);
                while (!File.Exists(path + ".pending") && !process.HasExited)
                { await Task.Delay(1, timeout.Token); }
                Assert.Equal("1234567890123456789", await previous.ReadToEndAsync(timeout.Token));
            }
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await errors);
            Assert.Equal(987654321L, await BrowserCheckpointFile.ReadAsync(path, timeout.Token));
            Assert.False(File.Exists(path + ".pending"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

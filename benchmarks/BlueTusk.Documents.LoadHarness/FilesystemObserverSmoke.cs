using System.Diagnostics;
using System.Text.Json;

namespace BlueTusk.Documents.LoadHarness;

internal static partial class Program
{
    private static async Task FilesystemObserverSmokeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bluetusk-documents-observer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "samples");
        Directory.CreateDirectory(path);
        var temporary = Path.Combine(path, "sample.tmp");
        var held = Path.Combine(directory, "sample.held");
        var sequence = 0;
        var budget = new StorageBudget(MaintenanceProfile.PackageDefaults, 24L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024, path, 0);
        async Task<string> WriteAsync(DateTimeOffset observed, long minimumAvailable)
        {
            var completed = Path.Combine(path, $"{++sequence:D6}.json");
            var sample = new FilesystemObservation(observed, 100L * 1024 * 1024 * 1024,
                10L * 1024 * 1024 * 1024, 90L * 1024 * 1024 * 1024, minimumAvailable, 10L * 1024 * 1024 * 1024);
            try
            {
                await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(sample, StorageJson.Default.FilesystemObservation));
                for (var attempt = 1; attempt <= 10; attempt++)
                {
                    try { File.Move(temporary, completed); return completed; }
                    catch (Exception exception) when (attempt < 10 && exception is IOException or UnauthorizedAccessException) { await Task.Delay(50); }
                }
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
            throw new InvalidOperationException("Bounded sample publication was exhausted.");
        }
        try
        {
            await WriteAsync(DateTimeOffset.UtcNow, 90L * 1024 * 1024 * 1024);
            var writer = Task.Run(async () =>
            {
                for (var index = 0; index < 1000; index++)
                {
                    try { await WriteAsync(DateTimeOffset.UtcNow, 90L * 1024 * 1024 * 1024); }
                    catch (UnauthorizedAccessException exception) { throw new InvalidOperationException($"immutable-sample writer iteration {index} failed", exception); }
                    if (index % 16 == 0) { await Task.Yield(); }
                }
            });
            var reads = 0; var retriedReads = 0;
            while (!writer.IsCompleted || reads < 1000)
            {
                var observed = await budget.ReadFilesystemAsync(CancellationToken.None);
                Check(observed.MinimumAvailableBytesObserved == 90L * 1024 * 1024 * 1024, "immutable publication retained the exact guard value");
                reads++; retriedReads += observed.ReadAttempts - 1;
                Check(reads < 10000, "bounded immutable-sample reader stress");
                await Task.Delay(1);
            }
            await writer;

            foreach (var file in Directory.EnumerateFiles(path, "*.json")) { File.Delete(file); }
            var transient = await WriteAsync(DateTimeOffset.UtcNow, 90L * 1024 * 1024 * 1024);
            File.Move(transient, held);
            var restore = Task.Run(async () => { await Task.Delay(100); File.Move(held, transient); });
            var recovered = await budget.ReadFilesystemAsync(CancellationToken.None);
            await restore;
            Check(recovered.ReadAttempts > 1, "transient missing sample recovered within bounded attempts");

            File.Delete(transient);
            var started = Stopwatch.GetTimestamp();
            var unavailable = false;
            try { _ = await budget.ReadFilesystemAsync(CancellationToken.None); }
            catch (StorageObservationException exception) when (exception.Code == "filesystem-observer-unavailable") { unavailable = true; }
            Check(unavailable && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2), "persistently missing sample fails closed promptly");

            await WriteAsync(DateTimeOffset.UtcNow.AddSeconds(-31), 90L * 1024 * 1024 * 1024);
            var stale = false;
            try { _ = await budget.ReadFilesystemAsync(CancellationToken.None); }
            catch (StorageObservationException exception) when (exception.Code == "filesystem-observer-stale") { stale = true; }
            Check(stale, "read retries never accept a stale sample");

            await WriteAsync(DateTimeOffset.UtcNow, 1);
            var lowHeadroom = false;
            try { _ = await budget.ReadFilesystemAsync(CancellationToken.None); }
            catch (StorageBudgetException exception) when (exception.Limit == "filesystem-headroom") { lowHeadroom = true; }
            Check(lowHeadroom, "read retries never bypass the free-space threshold");
            File.WriteAllText(Path.Combine(path, $"{++sequence:D6}.json"), "broken");
            var invalidNewest = false;
            try { _ = await budget.ReadFilesystemAsync(CancellationToken.None); }
            catch (StorageObservationException exception) when (exception.Code == "filesystem-observer-invalid") { invalidNewest = true; }
            Check(invalidNewest, "newest unreadable sample never falls back to an older one");
            for (var index = 3; index <= StorageBudget.MaximumFilesystemSamples; index++)
            { File.WriteAllText(Path.Combine(path, $"{++sequence:D6}.json"), "{}"); }
            var tooMany = false;
            try { _ = await budget.ReadFilesystemAsync(CancellationToken.None); }
            catch (StorageObservationException exception) when (exception.Code == "filesystem-observer-invalid") { tooMany = true; }
            Check(tooMany, "sample-count bound fails closed");
            Console.WriteLine($"Documents filesystem observer smoke passed: {reads} concurrent immutable-sample reads, {retriedReads} race retries, transient recovery, persistent-missing, stale, low-headroom, broken-newest and count-bound stops.");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(path)) { File.Delete(file); }
            Directory.Delete(path, recursive: false);
            if (File.Exists(held)) { File.Delete(held); }
            Directory.Delete(directory, recursive: false);
        }
    }
}

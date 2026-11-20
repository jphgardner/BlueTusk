using System.Globalization;

namespace BlueTusk.Edge.LoadHarness;

internal static class BrowserCheckpointFile
{
    internal static StreamReader OpenSnapshot(string path) => new(new FileStream(path, FileMode.Open,
        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan));

    internal static async Task<long?> ReadAsync(string path, CancellationToken token)
    {
        StreamReader reader;
        try { reader = OpenSnapshot(path); }
        catch (FileNotFoundException) { return null; }
        using (reader)
        {
#if NET7_0_OR_GREATER
            var text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
#else
            var text = await reader.ReadToEndAsync().WaitAsync(token).ConfigureAwait(false);
#endif
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
            { throw new InvalidOperationException("The browser published an invalid checkpoint snapshot."); }
            return position;
        }
    }
}

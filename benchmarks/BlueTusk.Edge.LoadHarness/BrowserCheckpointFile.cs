using System.Globalization;

namespace BlueTusk.Edge.LoadHarness;

internal static class BrowserCheckpointFile
{
    internal static StreamReader OpenSnapshot(string path) => new(new FileStream(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    }));

    internal static async Task<long?> ReadAsync(string path, CancellationToken token)
    {
        StreamReader reader;
        try { reader = OpenSnapshot(path); }
        catch (FileNotFoundException) { return null; }
        using (reader)
        {
            var text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
            { throw new InvalidOperationException("The browser published an invalid checkpoint snapshot."); }
            return position;
        }
    }
}

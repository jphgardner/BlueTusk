#if !NET8_0_OR_GREATER
namespace BlueTusk.Compatibility;

internal static class TextWriterCompatibilityExtensions
{
    public static async Task FlushAsync(this TextWriter writer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Old TextWriter has no cancellable flush. Wait for its write to finish
        // before reporting cancellation, so disposal cannot race an in-flight write.
        await writer.FlushAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
#endif

#if !NET8_0_OR_GREATER
namespace BlueTusk.Compatibility;

internal static class CancellationTokenSourceCompatibilityExtensions
{
    public static Task CancelAsync(this CancellationTokenSource source) => Task.Run(source.Cancel);
}
#endif

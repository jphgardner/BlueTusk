#if !NET8_0_OR_GREATER
namespace BlueTusk.Compatibility;

internal static class SpanCompatibilityExtensions
{
    public static int IndexOfAnyExcept(this ReadOnlySpan<char> value, ReadOnlySpan<char> allowed)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!allowed.Contains(value[index]))
            {
                return index;
            }
        }

        return -1;
    }
}
#endif

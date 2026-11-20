#if !NET7_0_OR_GREATER
namespace BlueTusk.Compatibility;

internal static class StreamCompatibilityExtensions
{
    public static void ReadExactly(this Stream stream, Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var read = stream.Read(buffer);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            buffer = buffer[read..];
        }
    }

    public static async ValueTask ReadExactlyAsync(this Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            buffer = buffer[read..];
        }
    }
}
#endif

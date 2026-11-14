using System.Runtime.CompilerServices;
using System.Security.Cryptography;

[assembly: InternalsVisibleTo("BlueTusk.Studio.ControlPlane")]
[assembly: InternalsVisibleTo("BlueTusk.Studio.Events")]

namespace BlueTusk.Studio;

internal static class StudioOperationId
{
    public static bool IsVersion7(Guid value)
    {
#if NET9_0_OR_GREATER
        return value.Version == 7;
#else
        Span<byte> bytes = stackalloc byte[16];
        _ = BlueTuskRuntime.TryWriteGuidBigEndian(value, bytes, out _);
        return (bytes[6] >> 4) == 7;
#endif
    }

    public static Guid CreateVersion7()
    {
#if NET9_0_OR_GREATER
        return Guid.CreateVersion7();
#else
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var index = 5; index >= 0; index--)
        {
            bytes[index] = (byte)milliseconds;
            milliseconds >>= 8;
        }

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return BlueTuskRuntime.ReadGuidBigEndian(bytes);
#endif
    }
}

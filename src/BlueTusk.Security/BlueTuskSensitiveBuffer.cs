using System.Security.Cryptography;

namespace BlueTusk.Security;

/// <summary>Helpers for clearing writable buffers that held authentication material.</summary>
public static class BlueTuskSensitiveBuffer
{
    public static void Clear(Span<byte> buffer) => CryptographicOperations.ZeroMemory(buffer);

    public static void Clear(byte[]? buffer)
    {
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}


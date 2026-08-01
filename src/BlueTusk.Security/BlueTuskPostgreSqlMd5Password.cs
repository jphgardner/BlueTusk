using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Security;

/// <summary>Creates PostgreSQL's legacy MD5 password challenge response.</summary>
/// <remarks>
/// This algorithm exists only for compatibility with servers that still request PostgreSQL MD5
/// authentication. SCRAM-SHA-256 should be used for new deployments.
/// </remarks>
public static class BlueTuskPostgreSqlMd5Password
{
    private const int DigestLength = 16;

    [SuppressMessage(
        "Security",
        "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "PostgreSQL's legacy wire protocol explicitly requires MD5 for this compatibility path.")]
    public static byte[] CreateResponse(string username, string password, ReadOnlySpan<byte> salt)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        if (salt.Length != sizeof(int))
        {
            throw new ArgumentException("A PostgreSQL MD5 authentication salt must contain exactly four bytes.", nameof(salt));
        }

        var passwordLength = Encoding.UTF8.GetByteCount(password);
        var usernameLength = Encoding.UTF8.GetByteCount(username);
        var identity = new byte[checked(passwordLength + usernameLength)];
        Span<byte> firstDigest = stackalloc byte[DigestLength];
        Span<byte> firstHex = stackalloc byte[DigestLength * 2];
        Span<byte> secondInput = stackalloc byte[(DigestLength * 2) + sizeof(int)];
        Span<byte> secondDigest = stackalloc byte[DigestLength];
        var response = new byte[3 + (DigestLength * 2)];

        try
        {
            Encoding.UTF8.GetBytes(password, identity);
            Encoding.UTF8.GetBytes(username, identity.AsSpan(passwordLength));
            _ = MD5.HashData(identity, firstDigest);
            WriteLowerHex(firstDigest, firstHex);
            firstHex.CopyTo(secondInput);
            salt.CopyTo(secondInput[(DigestLength * 2)..]);
            _ = MD5.HashData(secondInput, secondDigest);
            "md5"u8.CopyTo(response);
            WriteLowerHex(secondDigest, response.AsSpan(3));
            return response;
        }
        finally
        {
            BlueTuskSensitiveBuffer.Clear(identity);
            BlueTuskSensitiveBuffer.Clear(firstDigest);
            BlueTuskSensitiveBuffer.Clear(firstHex);
            BlueTuskSensitiveBuffer.Clear(secondInput);
            BlueTuskSensitiveBuffer.Clear(secondDigest);
        }
    }

    private static void WriteLowerHex(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        const string Hex = "0123456789abcdef";
        for (var index = 0; index < source.Length; index++)
        {
            destination[index * 2] = (byte)Hex[source[index] >> 4];
            destination[(index * 2) + 1] = (byte)Hex[source[index] & 0x0f];
        }
    }
}

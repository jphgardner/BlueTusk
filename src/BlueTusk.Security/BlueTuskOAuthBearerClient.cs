using System.Text;

namespace BlueTusk.Security;

/// <summary>Builds PostgreSQL's RFC 7628 OAUTHBEARER client response.</summary>
public static class BlueTuskOAuthBearerClient
{
    public const string MechanismName = "OAUTHBEARER";

    private static ReadOnlySpan<byte> ResponsePrefix => "n,,\u0001auth=Bearer "u8;

    private static ReadOnlySpan<byte> ResponseSuffix => "\u0001\u0001"u8;

    /// <summary>Builds a sensitive initial client response from an existing bearer token.</summary>
    /// <remarks>The caller must clear the returned array after it has been sent.</remarks>
    public static byte[] CreateInitialResponse(string accessToken)
    {
        ArgumentNullException.ThrowIfNull(accessToken);
        ValidateAccessToken(accessToken);

        var response = new byte[checked(ResponsePrefix.Length + accessToken.Length + ResponseSuffix.Length)];
        ResponsePrefix.CopyTo(response);
        Encoding.ASCII.GetBytes(accessToken, response.AsSpan(ResponsePrefix.Length, accessToken.Length));
        ResponseSuffix.CopyTo(response.AsSpan(ResponsePrefix.Length + accessToken.Length));
        return response;
    }

    private static void ValidateAccessToken(string accessToken)
    {
        if (accessToken.Length == 0)
        {
            throw new BlueTuskAuthenticationException(
                "The configured access-token provider returned an empty bearer token.");
        }

        var padding = false;
        foreach (var character in accessToken)
        {
            if (character == '=')
            {
                padding = true;
                continue;
            }

            if (padding || !IsTokenCharacter(character))
            {
                throw new BlueTuskAuthenticationException(
                    "The configured access-token provider returned an invalid RFC 6750 bearer token.");
            }
        }
    }

    private static bool IsTokenCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or
            '-' or '.' or '_' or '~' or '+' or '/';
}

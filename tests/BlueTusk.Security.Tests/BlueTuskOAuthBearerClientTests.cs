using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Security.Tests;

public sealed class BlueTuskOAuthBearerClientTests
{
    [Fact]
    public void Builds_the_RFC_7628_initial_client_response()
    {
        var response = BlueTuskOAuthBearerClient.CreateInitialResponse("header.payload.signature");
        try
        {
            Assert.Equal(
                "n,,\u0001auth=Bearer header.payload.signature\u0001\u0001",
                Encoding.ASCII.GetString(response));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("token with spaces")]
    [InlineData("token\u0001injection")]
    [InlineData("padded=then-data")]
    [InlineData("non-ascii-£")]
    public void Rejects_values_outside_the_RFC_6750_bearer_token_grammar(string token)
    {
        var exception = Assert.Throws<BlueTuskAuthenticationException>(
            () => BlueTuskOAuthBearerClient.CreateInitialResponse(token));

        if (token.Length != 0)
        {
            Assert.DoesNotContain(token, exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("AZaz09-._~+/")]
    [InlineData("opaque-token==")]
    public void Accepts_RFC_6750_bearer_token_characters(string token)
    {
        var response = BlueTuskOAuthBearerClient.CreateInitialResponse(token);
        CryptographicOperations.ZeroMemory(response);
    }
}

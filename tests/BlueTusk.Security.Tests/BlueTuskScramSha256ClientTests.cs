namespace BlueTusk.Security.Tests;

public sealed class BlueTuskScramSha256ClientTests
{
    private const string ClientNonce = "rOprNGfwEbeRWgbNEkqO";
    private const string ServerFirst =
        "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096";

    [Fact]
    public void Matches_the_Rfc7677_exchange_vector()
    {
        using var client = new BlueTuskScramSha256Client("user", "pencil", ClientNonce);

        Assert.Equal("SCRAM-SHA-256", client.Mechanism);
        Assert.Equal("n,,n=user,r=rOprNGfwEbeRWgbNEkqO", client.ClientFirstMessage);
        Assert.Equal(
            "c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,p=dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=",
            client.CreateClientFinalMessage(ServerFirst));

        client.VerifyServerFinalMessage("v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=");
        client.EnsureVerified();
    }

    [Fact]
    public void Escapes_the_authentication_identity()
    {
        using var client = new BlueTuskScramSha256Client("a,b=c", "secret", ClientNonce);

        Assert.StartsWith("n,,n=a=2Cb=3Dc,r=", client.ClientFirstMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_server_nonce_that_does_not_extend_the_client_nonce()
    {
        using var client = new BlueTuskScramSha256Client("user", "pencil", ClientNonce);

        Assert.Throws<BlueTuskAuthenticationException>(
            () => client.CreateClientFinalMessage("r=attacker,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096"));
    }

    [Fact]
    public void Rejects_an_invalid_server_signature()
    {
        using var client = new BlueTuskScramSha256Client("user", "pencil", ClientNonce);
        _ = client.CreateClientFinalMessage(ServerFirst);

        Assert.Throws<BlueTuskAuthenticationException>(
            () => client.VerifyServerFinalMessage("v=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));
    }

    [Fact]
    public void Uses_plus_when_channel_binding_data_is_available()
    {
        using var client = new BlueTuskScramSha256Client(
            "user",
            "pencil",
            ClientNonce,
            new byte[] { 1, 2, 3 });

        Assert.Equal("SCRAM-SHA-256-PLUS", client.Mechanism);
        Assert.StartsWith("p=tls-server-end-point,,", client.ClientFirstMessage, StringComparison.Ordinal);
    }
}


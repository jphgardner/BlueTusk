namespace BlueTusk.Protocol;

public abstract record BlueTuskAuthenticationRequest
{
    private BlueTuskAuthenticationRequest()
    {
    }

    public sealed record Ok : BlueTuskAuthenticationRequest;

    public sealed record CleartextPassword : BlueTuskAuthenticationRequest;

    public sealed record Md5Password(ReadOnlyMemory<byte> Salt) : BlueTuskAuthenticationRequest;

    public sealed record Sasl(IReadOnlyList<string> Mechanisms) : BlueTuskAuthenticationRequest;

    public sealed record SaslContinue(string Data) : BlueTuskAuthenticationRequest;

    public sealed record SaslFinal(string Data) : BlueTuskAuthenticationRequest;
}


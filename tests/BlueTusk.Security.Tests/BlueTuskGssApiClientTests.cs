using System.Net.Security;

namespace BlueTusk.Security.Tests;

public sealed class BlueTuskGssApiClientTests
{
    [Fact]
    public void Completes_a_mutually_authenticated_multistep_exchange()
    {
        using var engine = new FakeNegotiateAuthentication(
            (new byte[] { 1, 2 }, NegotiateAuthenticationStatusCode.ContinueNeeded),
            (new byte[] { 3, 4 }, NegotiateAuthenticationStatusCode.Completed));
        using var client = new BlueTuskGssApiClient(engine);

        var first = client.GetOutgoingBlob(ReadOnlySpan<byte>.Empty);
        var second = client.GetOutgoingBlob(new byte[] { 9, 8 });
        client.EnsureComplete();

        Assert.Equal(new byte[] { 1, 2 }, first);
        Assert.Equal(new byte[] { 3, 4 }, second);
        Assert.Equal([Array.Empty<byte>(), new byte[] { 9, 8 }], engine.IncomingBlobs);
    }

    [Fact]
    public void Allows_completion_without_a_final_outgoing_token()
    {
        using var engine = new FakeNegotiateAuthentication(
            (null, NegotiateAuthenticationStatusCode.Completed));
        using var client = new BlueTuskGssApiClient(engine);

        var response = client.GetOutgoingBlob(ReadOnlySpan<byte>.Empty);
        client.EnsureComplete();

        Assert.Null(response);
    }

    [Fact]
    public void Rejects_continuation_without_an_outgoing_token()
    {
        using var engine = new FakeNegotiateAuthentication(
            (null, NegotiateAuthenticationStatusCode.ContinueNeeded));
        using var client = new BlueTuskGssApiClient(engine);

        var exception = Assert.Throws<BlueTuskAuthenticationException>(
            () => client.GetOutgoingBlob(ReadOnlySpan<byte>.Empty));

        Assert.Contains("without producing a token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clears_tokens_returned_with_a_failure_status()
    {
        var rejectedToken = new byte[] { 7, 8, 9 };
        using var engine = new FakeNegotiateAuthentication(
            (rejectedToken, NegotiateAuthenticationStatusCode.InvalidCredentials));
        using var client = new BlueTuskGssApiClient(engine);

        var exception = Assert.Throws<BlueTuskAuthenticationException>(
            () => client.GetOutgoingBlob(ReadOnlySpan<byte>.Empty));

        Assert.Equal(new byte[3], rejectedToken);
        Assert.DoesNotContain("7", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_operating_system_provider_failures()
    {
        using var engine = new ThrowingNegotiateAuthentication();
        using var client = new BlueTuskGssApiClient(engine);

        var exception = Assert.Throws<BlueTuskAuthenticationException>(
            () => client.GetOutgoingBlob(ReadOnlySpan<byte>.Empty));

        Assert.DoesNotContain("credential-must-not-escape", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Rejects_a_context_without_mutual_authentication()
    {
        using var engine = new FakeNegotiateAuthentication(
            isMutuallyAuthenticated: false,
            (null, NegotiateAuthenticationStatusCode.Completed));
        using var client = new BlueTuskGssApiClient(engine);
        _ = client.GetOutgoingBlob(ReadOnlySpan<byte>.Empty);

        var exception = Assert.Throws<BlueTuskAuthenticationException>(client.EnsureComplete);

        Assert.Contains("mutually authenticate", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakeNegotiateAuthentication : IBlueTuskNegotiateAuthentication
    {
        private readonly Queue<(byte[]? Blob, NegotiateAuthenticationStatusCode Status)> _responses;
        private readonly bool _isMutuallyAuthenticated;

        internal FakeNegotiateAuthentication(
            params (byte[]? Blob, NegotiateAuthenticationStatusCode Status)[] responses)
            : this(isMutuallyAuthenticated: true, responses)
        {
        }

        internal FakeNegotiateAuthentication(
            bool isMutuallyAuthenticated,
            params (byte[]? Blob, NegotiateAuthenticationStatusCode Status)[] responses)
        {
            _isMutuallyAuthenticated = isMutuallyAuthenticated;
            _responses = new Queue<(byte[]?, NegotiateAuthenticationStatusCode)>(responses);
        }

        public List<byte[]> IncomingBlobs { get; } = [];

        public bool IsAuthenticated { get; private set; }

        public bool IsMutuallyAuthenticated => IsAuthenticated && _isMutuallyAuthenticated;

        public byte[]? GetOutgoingBlob(
            ReadOnlySpan<byte> incomingBlob,
            out NegotiateAuthenticationStatusCode statusCode)
        {
            IncomingBlobs.Add(incomingBlob.ToArray());
            var response = _responses.Dequeue();
            statusCode = response.Status;
            IsAuthenticated = statusCode == NegotiateAuthenticationStatusCode.Completed;
            return response.Blob;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingNegotiateAuthentication : IBlueTuskNegotiateAuthentication
    {
        public bool IsAuthenticated => false;

        public bool IsMutuallyAuthenticated => false;

        public byte[]? GetOutgoingBlob(
            ReadOnlySpan<byte> incomingBlob,
            out NegotiateAuthenticationStatusCode statusCode)
        {
            statusCode = NegotiateAuthenticationStatusCode.GenericFailure;
            throw new InvalidOperationException("credential-must-not-escape");
        }

        public void Dispose()
        {
        }
    }
}

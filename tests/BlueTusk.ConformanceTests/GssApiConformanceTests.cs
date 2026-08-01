using System.Buffers.Binary;
using System.Net.Security;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Security;

namespace BlueTusk.ConformanceTests;

public sealed class GssApiConformanceTests
{
    [Fact]
    public void Low_level_options_parse_and_redact_GSSAPI_configuration()
    {
        var options = BlueTuskClientOptions.FromConnectionString(
            "Host=db.example.test;Database=app;Username=worker;Password=secret;" +
            "Kerberos Service Name=postgresql");

        Assert.Equal("postgresql", options.KerberosServiceName);
        Assert.DoesNotContain("secret", options.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(
            () => (options with { KerberosServiceName = "bad/name" }).Validate());
    }

    [Fact]
    public async Task Authenticates_with_a_multistep_GSSAPI_exchange_asynchronously()
    {
        var initialToken = new byte[] { 1, 2, 3 };
        var serverToken = new byte[] { 4, 5, 6 };
        var finalToken = new byte[] { 7, 8, 9 };
        var engine = new FakeNegotiateAuthentication(
            (initialToken.ToArray(), NegotiateAuthenticationStatusCode.ContinueNeeded),
            (finalToken.ToArray(), NegotiateAuthenticationStatusCode.Completed));
        var usedSspi = true;
        var passwordCalls = 0;
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(AuthenticationRequest(7)),
            new FakeServerStep.ExpectFrontendMessage((byte)'p', initialToken),
            new FakeServerStep.Send(AuthenticationRequest(8, serverToken)),
            new FakeServerStep.ExpectFrontendMessage((byte)'p', finalToken),
            new FakeServerStep.Send(StartupComplete()),
        ], CancellationToken.None);

        await using var session = await BlueTuskSession.OpenAsync(
            Options(server.Port) with
            {
                Password = null,
                PasswordProvider = _ =>
                {
                    passwordCalls++;
                    throw new InvalidOperationException("must not run");
                },
                GssApiClientFactory = isSspi =>
                {
                    usedSspi = isSspi;
                    return new BlueTuskGssApiClient(engine);
                },
            });

        Assert.True(session.IsOpen);
        Assert.False(usedSspi);
        Assert.Equal(0, passwordCalls);
        Assert.Equal([Array.Empty<byte>(), serverToken], engine.IncomingBlobs);
        await serverTask;
    }

    [Fact]
    public async Task Authenticates_with_an_SSPI_exchange_synchronously()
    {
        var token = new byte[] { 10, 11, 12 };
        var engine = new FakeNegotiateAuthentication(
            (token.ToArray(), NegotiateAuthenticationStatusCode.Completed));
        var usedSspi = false;
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(AuthenticationRequest(9)),
            new FakeServerStep.ExpectFrontendMessage((byte)'p', token),
            new FakeServerStep.Send(StartupComplete()),
        ], CancellationToken.None);

        using var session = BlueTuskSession.Open(
            Options(server.Port) with
            {
                GssApiClientFactory = isSspi =>
                {
                    usedSspi = isSspi;
                    return new BlueTuskGssApiClient(engine);
                },
            });

        Assert.True(session.IsOpen);
        Assert.True(usedSspi);
        await serverTask;
    }

    [Fact]
    public async Task Rejects_a_GSSAPI_failure_without_exposing_provider_tokens()
    {
        var rejectedToken = Encoding.UTF8.GetBytes("credential-token-must-not-escape");
        var engine = new FakeNegotiateAuthentication(
            (rejectedToken, NegotiateAuthenticationStatusCode.InvalidCredentials));
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(AuthenticationRequest(7)),
        ], CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BlueTuskAuthenticationException>(
            () => BlueTuskSession.OpenAsync(
                Options(server.Port) with
                {
                    GssApiClientFactory = _ => new BlueTuskGssApiClient(engine),
                }).AsTask());

        Assert.DoesNotContain("credential-token-must-not-escape", exception.ToString(), StringComparison.Ordinal);
        Assert.All(rejectedToken, static value => Assert.Equal(0, value));
        await serverTask;
    }

    [Fact]
    public async Task Rejects_AuthenticationOk_before_GSSAPI_completion()
    {
        var engine = new FakeNegotiateAuthentication(
            (new byte[] { 1 }, NegotiateAuthenticationStatusCode.ContinueNeeded));
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(AuthenticationRequest(7)),
            new FakeServerStep.ExpectFrontendMessage((byte)'p', new byte[] { 1 }),
            new FakeServerStep.Send(StartupComplete()),
        ], CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BlueTuskAuthenticationException>(
            () => BlueTuskSession.OpenAsync(
                Options(server.Port) with
                {
                    GssApiClientFactory = _ => new BlueTuskGssApiClient(engine),
                }).AsTask());

        Assert.Contains("before the GSSAPI security context", exception.Message, StringComparison.Ordinal);
        await serverTask;
    }

    private static BlueTuskClientOptions Options(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Database = "app",
        Username = "user",
        Password = "unused",
        SslMode = BlueTuskSslMode.Disable,
        ChannelBinding = BlueTuskChannelBindingMode.Disable,
    };

    private static byte[] AuthenticationRequest(int code, byte[]? data = null) =>
        Frame('R', Combine(Int32(code), data ?? []));

    private static byte[] StartupComplete() => Combine(
        Frame('R', Int32(0)),
        Frame('S', CStrings("server_version", "18.0")),
        Frame('K', Combine(Int32(123), Int32(456))),
        Frame('Z', [(byte)'I']));

    private static byte[] Frame(char identifier, byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = (byte)identifier;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + sizeof(int));
        payload.CopyTo(frame, 5);
        return frame;
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] CStrings(params string[] values) =>
        values.SelectMany(value => Encoding.UTF8.GetBytes(value).Append((byte)0)).ToArray();

    private static byte[] Combine(params byte[][] values)
    {
        var result = new byte[values.Sum(static value => value.Length)];
        var offset = 0;
        foreach (var value in values)
        {
            value.CopyTo(result, offset);
            offset += value.Length;
        }

        return result;
    }

    private sealed class FakeNegotiateAuthentication : IBlueTuskNegotiateAuthentication
    {
        private readonly Queue<(byte[]? Blob, NegotiateAuthenticationStatusCode Status)> _responses;

        internal FakeNegotiateAuthentication(
            params (byte[]? Blob, NegotiateAuthenticationStatusCode Status)[] responses)
        {
            _responses = new Queue<(byte[]?, NegotiateAuthenticationStatusCode)>(responses);
        }

        public List<byte[]> IncomingBlobs { get; } = [];

        public bool IsAuthenticated { get; private set; }

        public bool IsMutuallyAuthenticated => IsAuthenticated;

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
}

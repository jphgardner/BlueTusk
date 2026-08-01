using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Protocol;
using BlueTusk.Security;

namespace BlueTusk.ConformanceTests;

public sealed class OAuthBearerConformanceTests
{
    private const string AccessToken = "header.payload.signature";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAUTHBEARER_sends_an_existing_token_over_TLS(bool asynchronous)
    {
        using var certificate = CreateServerCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = RunServerAsync(listener, certificate, sendErrorChallenge: false);
        var calls = 0;
        var options = Options(port, certificate) with
        {
            AccessTokenProvider = _ =>
            {
                calls++;
                return AccessToken;
            },
            AccessTokenProviderAsync = (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(AccessToken);
            },
        };

        if (asynchronous)
        {
            await using var session = await BlueTuskSession.OpenAsync(options);
            Assert.True(session.IsOpen);
            Assert.True(session.IsEncrypted);
            Assert.True(session.Capabilities.SupportsOAuthBearer);
        }
        else
        {
            using var session = BlueTuskSession.Open(options);
            Assert.True(session.IsOpen);
            Assert.True(session.IsEncrypted);
            Assert.True(session.Capabilities.SupportsOAuthBearer);
        }

        Assert.Equal(1, calls);
        await serverTask;
    }

    [Fact]
    public async Task OAUTHBEARER_acknowledges_a_server_error_challenge_without_replaying_the_token()
    {
        using var certificate = CreateServerCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = RunServerAsync(listener, certificate, sendErrorChallenge: true);

        var exception = await Assert.ThrowsAsync<BlueTuskServerException>(
            () => BlueTuskSession.OpenAsync(
                Options(port, certificate) with
                {
                    AccessTokenProviderAsync = (_, _) => ValueTask.FromResult(AccessToken),
                }).AsTask());

        Assert.Equal("28000", exception.SqlState);
        Assert.DoesNotContain(AccessToken, exception.ToString(), StringComparison.Ordinal);
        await serverTask;
    }

    private static async Task RunServerAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        bool sendErrorChallenge)
    {
        using var socket = await listener.AcceptSocketAsync(CancellationToken.None);
        await using var network = new NetworkStream(socket, ownsSocket: false);
        var sslRequest = await ReadUnframedMessageAsync(network);
        Assert.Equal(BlueTuskFrontendMessageWriter.SslRequestCode, BinaryPrimitives.ReadInt32BigEndian(sslRequest));
        await network.WriteAsync(new byte[] { (byte)'S' });
        await network.FlushAsync();

        await using var tls = new SslStream(network, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.None,
            },
            CancellationToken.None);

        _ = await ReadUnframedMessageAsync(tls);
        await tls.WriteAsync(AuthenticationRequest(10, CStrings("OAUTHBEARER", string.Empty)));
        await tls.FlushAsync();

        var initialResponse = BlueTuskOAuthBearerClient.CreateInitialResponse(AccessToken);
        try
        {
            var expected = Combine(
                CStrings(BlueTuskOAuthBearerClient.MechanismName),
                Int32(initialResponse.Length),
                initialResponse);
            Assert.Equal(expected, await ReadFrontendMessageAsync(tls, (byte)'p'));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(initialResponse);
        }

        if (!sendErrorChallenge)
        {
            await tls.WriteAsync(StartupComplete());
            await tls.FlushAsync();
            return;
        }

        await tls.WriteAsync(AuthenticationRequest(
            11,
            Encoding.UTF8.GetBytes(
                "{\"status\":\"invalid_token\",\"scope\":\"database.connect\"}")));
        await tls.FlushAsync();
        Assert.Equal(new byte[] { 1 }, await ReadFrontendMessageAsync(tls, (byte)'p'));
        await tls.WriteAsync(OAuthError());
        await tls.FlushAsync();
    }

    private static BlueTuskClientOptions Options(int port, X509Certificate2 certificate) => new()
    {
        Host = "localhost",
        Port = port,
        Database = "app",
        Username = "oauth-user",
        Password = null,
        SslMode = BlueTuskSslMode.Require,
        ChannelBinding = BlueTuskChannelBindingMode.Disable,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        RemoteCertificateValidationCallback = (_, presented, _, _) =>
            string.Equals(
                presented?.GetCertHashString(),
                certificate.GetCertHashString(),
                StringComparison.Ordinal),
    };

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                critical: false));
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
    }

    private static async Task<byte[]> ReadUnframedMessageAsync(Stream stream)
    {
        var lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        var payload = new byte[length - sizeof(int)];
        await stream.ReadExactlyAsync(payload);
        return payload;
    }

    private static async Task<byte[]> ReadFrontendMessageAsync(Stream stream, byte identifier)
    {
        var header = new byte[sizeof(int) + 1];
        await stream.ReadExactlyAsync(header);
        Assert.Equal(identifier, header[0]);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        var payload = new byte[length - sizeof(int)];
        await stream.ReadExactlyAsync(payload);
        return payload;
    }

    private static byte[] AuthenticationRequest(int code, byte[] data) =>
        Frame('R', Combine(Int32(code), data));

    private static byte[] StartupComplete() => Combine(
        AuthenticationRequest(0, []),
        Frame('S', CStrings("server_version", "18.0")),
        Frame('K', Combine(Int32(123), Int32(456))),
        Frame('Z', [(byte)'I']));

    private static byte[] OAuthError() => Frame(
        'E',
        Combine(
            [(byte)'S'], CStrings("FATAL"),
            [(byte)'C'], CStrings("28000"),
            [(byte)'M'], CStrings("OAuth bearer token is invalid"),
            [0]));

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
}

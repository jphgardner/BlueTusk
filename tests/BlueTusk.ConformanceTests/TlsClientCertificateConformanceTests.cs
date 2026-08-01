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

namespace BlueTusk.ConformanceTests;

public sealed class TlsClientCertificateConformanceTests
{
    [Fact]
    public async Task PostgreSQL_SSLRequest_and_startup_offer_the_selected_client_certificate()
    {
        using var serverCertificate = CreateCertificate(
            "localhost",
            "1.3.6.1.5.5.7.3.1",
            addLocalhostSubjectAlternativeName: true);
        using var clientCertificate = CreateCertificate(
            "bluetusk-client",
            "1.3.6.1.5.5.7.3.2",
            addLocalhostSubjectAlternativeName: false);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = RunServerAsync(
            listener,
            serverCertificate,
            clientCertificate,
            CancellationToken.None);
        var selectionCalls = 0;

        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = "localhost",
                Port = port,
                Database = "app",
                Username = "certificate-user",
                Password = null,
                SslMode = BlueTuskSslMode.Require,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ClientCertificates = [clientCertificate],
                LocalCertificateSelectionCallback = (_, _, certificates, _, _) =>
                {
                    selectionCalls++;
                    return certificates[0];
                },
                RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    string.Equals(
                        presented?.GetCertHashString(),
                        serverCertificate.GetCertHashString(),
                        StringComparison.Ordinal),
                PasswordProvider = _ => throw new InvalidOperationException(
                    "Certificate authentication must not resolve a password."),
            });

        Assert.True(session.IsOpen);
        Assert.True(session.IsEncrypted);
        Assert.True(selectionCalls > 0);
        await serverTask;
    }

    private static async Task RunServerAsync(
        TcpListener listener,
        X509Certificate2 serverCertificate,
        X509Certificate2 expectedClientCertificate,
        CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptSocketAsync(cancellationToken);
        await using var network = new NetworkStream(socket, ownsSocket: false);
        var sslRequest = new byte[sizeof(int) * 2];
        await network.ReadExactlyAsync(sslRequest, cancellationToken);
        Assert.Equal(8, BinaryPrimitives.ReadInt32BigEndian(sslRequest));
        Assert.Equal(
            BlueTuskFrontendMessageWriter.SslRequestCode,
            BinaryPrimitives.ReadInt32BigEndian(sslRequest.AsSpan(sizeof(int))));
        await network.WriteAsync(new byte[] { (byte)'S' }, cancellationToken);
        await network.FlushAsync(cancellationToken);

        await using var tls = new SslStream(network, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    string.Equals(
                        presented?.GetCertHashString(),
                        expectedClientCertificate.GetCertHashString(),
                        StringComparison.Ordinal),
            },
            cancellationToken);

        var startupLengthBytes = new byte[sizeof(int)];
        await tls.ReadExactlyAsync(startupLengthBytes, cancellationToken);
        var startupLength = BinaryPrimitives.ReadInt32BigEndian(startupLengthBytes);
        Assert.True(startupLength > sizeof(int));
        var startupPayload = new byte[startupLength - sizeof(int)];
        await tls.ReadExactlyAsync(startupPayload, cancellationToken);
        Assert.Equal(
            BlueTuskFrontendMessageWriter.ProtocolVersion30,
            BinaryPrimitives.ReadInt32BigEndian(startupPayload));
        Assert.Contains(
            "certificate-user",
            Encoding.UTF8.GetString(startupPayload),
            StringComparison.Ordinal);

        await tls.WriteAsync(StartupComplete(), cancellationToken);
        await tls.FlushAsync(cancellationToken);
    }

    private static X509Certificate2 CreateCertificate(
        string commonName,
        string enhancedKeyUsage,
        bool addLocalhostSubjectAlternativeName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new(enhancedKeyUsage) },
                critical: false));
        if (addLocalhostSubjectAlternativeName)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
        }

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
    }

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
}

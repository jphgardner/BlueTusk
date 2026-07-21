using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BlueTusk.Transport.Tests;

public sealed class BlueTuskTlsTransportTests
{
    [Fact]
    public async Task Upgrades_an_established_socket_to_tls()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var ephemeralCertificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(
            ephemeralCertificate.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = RunServerAsync(listener, certificate);
            await using var transport = new BlueTuskSocketTransport();
            await transport.ConnectAsync(
                new BlueTuskEndpoint.Tcp("localhost", port),
                BlueTuskTransportOptions.Default,
                CancellationToken.None);

            try
            {
                await transport.UpgradeToTlsAsync(
                    new BlueTuskTlsOptions
                    {
                        TargetHost = "localhost",
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        RemoteCertificateValidationCallback = (_, presented, _, _) =>
                            string.Equals(
                                presented?.GetCertHashString(),
                                certificate.GetCertHashString(),
                                StringComparison.Ordinal),
                    },
                    CancellationToken.None);
            }
            catch (Exception clientException)
            {
                try
                {
                    await serverTask;
                }
                catch (Exception serverException)
                {
                    throw new AggregateException(clientException, serverException);
                }

                throw;
            }
            await transport.WriteAsync(new byte[] { 42 }, CancellationToken.None);
            await transport.FlushAsync(CancellationToken.None);

            Assert.True(transport.IsEncrypted);
            Assert.NotNull(transport.RemoteCertificate);
            Assert.Equal(42, await serverTask);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<int> RunServerAsync(TcpListener listener, X509Certificate2 certificate)
    {
        using var socket = await listener.AcceptSocketAsync(CancellationToken.None);
        await using var network = new NetworkStream(socket, ownsSocket: false);
        await using var tls = new SslStream(network, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.None,
            },
            CancellationToken.None);
        return tls.ReadByte();
    }
}

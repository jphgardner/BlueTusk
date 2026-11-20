#if !NET8_0_OR_GREATER
global using TcpListener = BlueTusk.Testing.Compatibility.DisposableTcpListener;
#endif
global using X509CertificateLoader = BlueTusk.Testing.Compatibility.TestCertificateLoader;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace BlueTusk.Testing.Compatibility;

#if !NET8_0_OR_GREATER
internal sealed class DisposableTcpListener : System.Net.Sockets.TcpListener, IDisposable
{
    public DisposableTcpListener(IPAddress address, int port)
        : base(address, port)
    {
    }

    public void Dispose() => Stop();

#if !NET6_0_OR_GREATER
    public Task<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken) =>
        AcceptTcpClientAsync().WaitAsync(cancellationToken);

    public Task<Socket> AcceptSocketAsync(CancellationToken cancellationToken) =>
        AcceptSocketAsync().WaitAsync(cancellationToken);
#endif
}
#endif

internal static class TestCertificateLoader
{
    public static X509Certificate2 LoadPkcs12(byte[] data, string? password, X509KeyStorageFlags flags)
    {
#if NET9_0_OR_GREATER
        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            data, password, flags);
#else
        return new X509Certificate2(data, password, flags);
#endif
    }
}

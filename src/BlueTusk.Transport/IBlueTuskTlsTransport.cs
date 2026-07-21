using System.Security.Cryptography.X509Certificates;

namespace BlueTusk.Transport;

/// <summary>A transport that can be upgraded to TLS after PostgreSQL accepts an SSLRequest.</summary>
public interface IBlueTuskTlsTransport : IBlueTuskTransport
{
    bool IsEncrypted { get; }

    X509Certificate2? RemoteCertificate { get; }

    ValueTask UpgradeToTlsAsync(BlueTuskTlsOptions options, CancellationToken cancellationToken);
}


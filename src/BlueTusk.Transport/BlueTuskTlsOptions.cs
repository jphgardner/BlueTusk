using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace BlueTusk.Transport;

/// <summary>Controls TLS authentication. Platform certificate validation is used unless a callback is explicitly supplied.</summary>
public sealed record BlueTuskTlsOptions
{
    public required string TargetHost { get; init; }

    public SslProtocols EnabledProtocols { get; init; } = SslProtocols.None;

    public X509RevocationMode CertificateRevocationCheckMode { get; init; } = X509RevocationMode.Online;

    public IReadOnlyCollection<X509Certificate2> ClientCertificates { get; init; } = [];

    public RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetHost);
        ArgumentNullException.ThrowIfNull(ClientCertificates);
    }
}


using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BlueTusk.Security;

/// <summary>Builds the RFC 5929 <c>tls-server-end-point</c> channel binding from a server certificate.</summary>
public static class BlueTuskTlsServerEndPoint
{
    public static byte[] Create(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var algorithm = certificate.SignatureAlgorithm.Value switch
        {
            "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
            "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };

        return certificate.GetCertHash(algorithm);
    }
}


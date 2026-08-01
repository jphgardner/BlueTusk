using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BlueTusk.Client;

namespace BlueTusk.Data;

internal sealed record BlueTuskClientConfiguration
{
    internal static BlueTuskClientConfiguration Empty { get; } = new();

    internal BlueTuskPasswordProvider? PasswordProvider { get; init; }

    internal BlueTuskPasswordProviderAsync? PasswordProviderAsync { get; init; }

    internal BlueTuskAccessTokenProvider? AccessTokenProvider { get; init; }

    internal BlueTuskAccessTokenProviderAsync? AccessTokenProviderAsync { get; init; }

    internal bool AccessTokenRequiresTls { get; init; }

    internal NetworkCredential? GssCredential { get; init; }

    internal IReadOnlyCollection<X509Certificate2> ClientCertificates { get; init; } = [];

    internal LocalCertificateSelectionCallback? LocalCertificateSelectionCallback { get; init; }

    internal RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; init; }

    internal BlueTuskClientOptions Apply(BlueTuskClientOptions options) => options with
    {
        PasswordProvider = PasswordProvider,
        PasswordProviderAsync = PasswordProviderAsync,
        AccessTokenProvider = AccessTokenProvider,
        AccessTokenProviderAsync = AccessTokenProviderAsync,
        AccessTokenRequiresTls = AccessTokenRequiresTls,
        GssCredential = GssCredential,
        ClientCertificates = ClientCertificates,
        LocalCertificateSelectionCallback = LocalCertificateSelectionCallback,
        RemoteCertificateValidationCallback = RemoteCertificateValidationCallback,
    };

    internal void Validate()
    {
        var hasPasswordProvider = PasswordProvider is not null || PasswordProviderAsync is not null;
        var hasAccessTokenProvider = AccessTokenProvider is not null || AccessTokenProviderAsync is not null;
        if (hasPasswordProvider && hasAccessTokenProvider)
        {
            throw new InvalidOperationException(
                "A data source cannot configure both password and access-token providers.");
        }
    }
}

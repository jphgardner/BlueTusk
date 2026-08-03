using Azure.Core;
using BlueTusk.Data;

namespace BlueTusk.Identity.Azure;

/// <summary>Configures Microsoft Entra authentication for Azure Database for PostgreSQL.</summary>
public static class BlueTuskAzureIdentityExtensions
{
    /// <summary>The public-cloud Azure Database for PostgreSQL OAuth scope.</summary>
    public const string DefaultScope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>
    /// Uses a platform, managed-identity, workload-identity, service-principal, or other Azure SDK
    /// credential to acquire a fresh database token for each new physical connection.
    /// </summary>
    public static BlueTuskDataSourceBuilder UseAzurePostgreSqlEntraAuthentication(
        this BlueTuskDataSourceBuilder builder,
        TokenCredential credential,
        string scope = DefaultScope)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var requestContext = new TokenRequestContext([scope]);

        return builder
            .UseAccessTokenProvider(_ => RequireToken(
                credential.GetToken(requestContext, CancellationToken.None).Token))
            .UseAccessTokenProvider(async (_, cancellationToken) => RequireToken(
                (await credential.GetTokenAsync(requestContext, cancellationToken)
                    .ConfigureAwait(false)).Token))
            .RequireTlsForAccessTokens();
    }

    private static string RequireToken(string? token) =>
        string.IsNullOrWhiteSpace(token)
            ? throw new InvalidOperationException("The Azure credential returned no PostgreSQL access token.")
            : token;
}

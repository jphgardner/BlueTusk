using BlueTusk.Data;
using Google.Apis.Auth.OAuth2;

namespace BlueTusk.Identity.GoogleCloud;

/// <summary>Configures IAM database authentication for Google Cloud SQL for PostgreSQL.</summary>
public static class BlueTuskGoogleCloudIdentityExtensions
{
    /// <summary>The OAuth scope used by Google Cloud SQL login tokens.</summary>
    public const string CloudSqlLoginScope = "https://www.googleapis.com/auth/sqlservice.login";

    /// <summary>
    /// Scopes a Google credential for Cloud SQL login and acquires a fresh token for each new
    /// asynchronously opened physical connection.
    /// </summary>
    public static BlueTuskDataSourceBuilder UseGoogleCloudSqlIamAuthentication(
        this BlueTuskDataSourceBuilder builder,
        GoogleCredential credential,
        string scope = CloudSqlLoginScope)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return builder.UseGoogleCloudSqlIamAuthentication(credential.CreateScoped(scope));
    }

    /// <summary>
    /// Uses an already configured Google token source. The source must issue Cloud SQL login
    /// tokens. Google credential acquisition is asynchronous, so synchronous opens are rejected.
    /// </summary>
    public static BlueTuskDataSourceBuilder UseGoogleCloudSqlIamAuthentication(
        this BlueTuskDataSourceBuilder builder,
        ITokenAccess tokenAccess)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tokenAccess);

        return builder
            .UseAccessTokenProvider(async (_, cancellationToken) => RequireToken(
                await tokenAccess.GetAccessTokenForRequestAsync(
                    authUri: null,
                    cancellationToken).ConfigureAwait(false)))
            .RequireTlsForAccessTokens();
    }

    private static string RequireToken(string? token) =>
        string.IsNullOrWhiteSpace(token)
            ? throw new InvalidOperationException("The Google credential returned no Cloud SQL login token.")
            : token;
}

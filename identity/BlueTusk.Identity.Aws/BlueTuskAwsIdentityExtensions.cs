using Amazon;
using Amazon.RDS.Util;
using Amazon.Runtime;
using BlueTusk.Client;
using BlueTusk.Data;

namespace BlueTusk.Identity.Aws;

/// <summary>Configures AWS RDS/Aurora PostgreSQL IAM database authentication.</summary>
public static class BlueTuskAwsIdentityExtensions
{
    /// <summary>Uses the AWS SDK's standard region and credential resolution chains.</summary>
    public static BlueTuskDataSourceBuilder UseAwsRdsIamAuthentication(
        this BlueTuskDataSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Configure(
            builder,
            static request => RDSAuthTokenGenerator.GenerateAuthToken(
                request.Host,
                request.Port,
                request.Username),
            static request => RDSAuthTokenGenerator.GenerateAuthTokenAsync(
                request.Host,
                request.Port,
                request.Username));
    }

    /// <summary>Uses an explicit AWS region and the SDK's standard credential resolution chain.</summary>
    public static BlueTuskDataSourceBuilder UseAwsRdsIamAuthentication(
        this BlueTuskDataSourceBuilder builder,
        RegionEndpoint region)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(region);
        return Configure(
            builder,
            request => RDSAuthTokenGenerator.GenerateAuthToken(
                region,
                request.Host,
                request.Port,
                request.Username),
            request => RDSAuthTokenGenerator.GenerateAuthTokenAsync(
                region,
                request.Host,
                request.Port,
                request.Username));
    }

    /// <summary>Uses explicit AWS credentials and the SDK's standard region resolution chain.</summary>
    public static BlueTuskDataSourceBuilder UseAwsRdsIamAuthentication(
        this BlueTuskDataSourceBuilder builder,
        AWSCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(credentials);
        return Configure(
            builder,
            request => RDSAuthTokenGenerator.GenerateAuthToken(
                credentials,
                request.Host,
                request.Port,
                request.Username),
            request => RDSAuthTokenGenerator.GenerateAuthTokenAsync(
                credentials,
                request.Host,
                request.Port,
                request.Username));
    }

    /// <summary>Uses explicit AWS credentials and region.</summary>
    public static BlueTuskDataSourceBuilder UseAwsRdsIamAuthentication(
        this BlueTuskDataSourceBuilder builder,
        AWSCredentials credentials,
        RegionEndpoint region)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(region);
        return Configure(
            builder,
            request => RDSAuthTokenGenerator.GenerateAuthToken(
                credentials,
                region,
                request.Host,
                request.Port,
                request.Username),
            request => RDSAuthTokenGenerator.GenerateAuthTokenAsync(
                credentials,
                region,
                request.Host,
                request.Port,
                request.Username));
    }

    private static BlueTuskDataSourceBuilder Configure(
        BlueTuskDataSourceBuilder builder,
        Func<BlueTuskCredentialRequest, string> synchronousFactory,
        Func<BlueTuskCredentialRequest, Task<string>> asynchronousFactory) =>
        builder
            .UseAccessTokenProvider(request => RequireToken(synchronousFactory(request)))
            .UseAccessTokenProvider(async (request, cancellationToken) =>
                RequireToken(await asynchronousFactory(request)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false)))
            .RequireTlsForAccessTokens();

    private static string RequireToken(string? token) =>
        string.IsNullOrWhiteSpace(token)
            ? throw new InvalidOperationException("The AWS SDK returned no RDS IAM authentication token.")
            : token;
}

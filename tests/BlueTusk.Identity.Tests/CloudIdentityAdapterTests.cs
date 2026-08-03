using Amazon;
using Amazon.Runtime;
using Azure.Core;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Identity.Aws;
using BlueTusk.Identity.Azure;
using BlueTusk.Identity.GoogleCloud;
using Google.Apis.Auth.OAuth2;

namespace BlueTusk.Identity.Tests;

public sealed class CloudIdentityAdapterTests
{
    [Fact]
    public async Task Aws_adapter_signs_each_requested_endpoint_for_sync_and_async_opens()
    {
        var credentials = new BasicAWSCredentials("AKIDEXAMPLE", "secret-key-must-not-escape");
        using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=example.cluster-abcdefghijkl.eu-west-2.rds.amazonaws.com;" +
                "Port=5544;Database=app;Username=worker;SSL Mode=Require")
            .UseAwsRdsIamAuthentication(credentials, RegionEndpoint.EUWest2)
            .Build();
        var options = dataSource.CreateDedicatedSessionOptions();
        var request = new BlueTuskCredentialRequest(
            options.Host,
            options.Port,
            options.Database,
            options.Username);

        var synchronous = options.AccessTokenProvider!(request);
        var asynchronous = await options.AccessTokenProviderAsync!(request, CancellationToken.None);

        Assert.True(options.AccessTokenRequiresTls);
        Assert.Contains($"{request.Host}:{request.Port}/", synchronous, StringComparison.Ordinal);
        Assert.Contains("Action=connect", synchronous, StringComparison.Ordinal);
        Assert.Contains("DBUser=worker", synchronous, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Credential=AKIDEXAMPLE", synchronous, StringComparison.Ordinal);
        Assert.Contains($"{request.Host}:{request.Port}/", asynchronous, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key-must-not-escape", synchronous, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key-must-not-escape", asynchronous, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Azure_adapter_requests_the_PostgreSQL_scope_for_sync_and_async_opens()
    {
        var credential = new RecordingAzureCredential();
        using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=example.postgres.database.azure.com;Database=app;Username=worker;SSL Mode=Require")
            .UseAzurePostgreSqlEntraAuthentication(credential)
            .Build();
        var options = dataSource.CreateDedicatedSessionOptions();
        var request = new BlueTuskCredentialRequest(
            options.Host,
            options.Port,
            options.Database,
            options.Username);

        var synchronous = options.AccessTokenProvider!(request);
        var asynchronous = await options.AccessTokenProviderAsync!(request, CancellationToken.None);

        Assert.Equal("azure-sync-token", synchronous);
        Assert.Equal("azure-async-token", asynchronous);
        Assert.True(options.AccessTokenRequiresTls);
        Assert.Equal(
            [BlueTuskAzureIdentityExtensions.DefaultScope],
            credential.RequestedScopes[0]);
        Assert.Equal(
            [BlueTuskAzureIdentityExtensions.DefaultScope],
            credential.RequestedScopes[1]);
    }

    [Fact]
    public async Task Google_adapter_uses_async_tokens_and_requires_TLS()
    {
        var tokenAccess = new RecordingGoogleTokenAccess();
        using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=127.0.0.1;Database=app;Username=worker;SSL Mode=Require")
            .UseGoogleCloudSqlIamAuthentication(tokenAccess)
            .Build();
        var options = dataSource.CreateDedicatedSessionOptions();
        var request = new BlueTuskCredentialRequest(
            options.Host,
            options.Port,
            options.Database,
            options.Username);

        var token = await options.AccessTokenProviderAsync!(request, CancellationToken.None);

        Assert.Equal("google-cloud-sql-token", token);
        Assert.Null(options.AccessTokenProvider);
        Assert.True(options.AccessTokenRequiresTls);
        Assert.Single(tokenAccess.AuthenticationUris);
        Assert.Null(tokenAccess.AuthenticationUris[0]);
    }

    private sealed class RecordingAzureCredential : TokenCredential
    {
        public List<string[]> RequestedScopes { get; } = [];

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            RequestedScopes.Add(requestContext.Scopes.ToArray());
            return new AccessToken("azure-sync-token", DateTimeOffset.UtcNow.AddMinutes(30));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            RequestedScopes.Add(requestContext.Scopes.ToArray());
            return ValueTask.FromResult(
                new AccessToken("azure-async-token", DateTimeOffset.UtcNow.AddMinutes(30)));
        }
    }

    private sealed class RecordingGoogleTokenAccess : ITokenAccess
    {
        public List<string?> AuthenticationUris { get; } = [];

        public Task<string> GetAccessTokenForRequestAsync(
            string? authUri = null,
            CancellationToken cancellationToken = default)
        {
            AuthenticationUris.Add(authUri);
            return Task.FromResult("google-cloud-sql-token");
        }
    }
}

using Azure.Identity;
using BlueTusk.Data;
using BlueTusk.Identity.Aws;
using BlueTusk.Identity.Azure;
using BlueTusk.Identity.GoogleCloud;
using Google.Apis.Auth.OAuth2;
using Xunit.Sdk;

namespace BlueTusk.Identity.Tests;

public sealed class CloudIdentityAcceptanceTests
{
    [Fact]
    public async Task Aws_RDS_IAM_authenticates_with_the_default_SDK_identity()
    {
        var connectionString = RequireConnectionString(
            "BLUETUSK_AWS_RDS_TEST_CONNECTION_STRING");
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
            .UseAwsRdsIamAuthentication()
            .Build();

        Assert.Equal(
            new BlueTuskConnectionStringBuilder(connectionString).Username,
            await ReadCurrentUserAsync(dataSource));
    }

    [Fact]
    public async Task Azure_PostgreSQL_Entra_authenticates_with_the_default_SDK_identity()
    {
        var connectionString = RequireConnectionString(
            "BLUETUSK_AZURE_POSTGRESQL_TEST_CONNECTION_STRING");
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
            .UseAzurePostgreSqlEntraAuthentication(new DefaultAzureCredential())
            .Build();

        Assert.Equal(
            new BlueTuskConnectionStringBuilder(connectionString).Username,
            await ReadCurrentUserAsync(dataSource));
    }

    [Fact]
    public async Task Google_Cloud_SQL_IAM_authenticates_with_application_default_credentials()
    {
        var connectionString = RequireConnectionString(
            "BLUETUSK_GOOGLE_CLOUD_SQL_TEST_CONNECTION_STRING");
        var credential = await GoogleCredential.GetApplicationDefaultAsync();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
            .UseGoogleCloudSqlIamAuthentication(credential)
            .Build();

        Assert.Equal(
            new BlueTuskConnectionStringBuilder(connectionString).Username,
            await ReadCurrentUserAsync(dataSource));
    }

    private static async Task<string> ReadCurrentUserAsync(BlueTuskDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new BlueTuskCommand("SELECT current_user", connection);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static string RequireConnectionString(string environmentVariable)
    {
        var connectionString = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip($"{environmentVariable} is not configured.")
            : connectionString;
    }
}

using System.Text;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Security;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskAuthenticationIntegrationTests
{
    [Fact]
    public async Task Legacy_MD5_authentication_executes_against_PostgreSQL()
    {
        var settings = GetSettings();
        await ExecuteAdminAsync(
            settings,
            """
            SET password_encryption = 'md5';
            DROP ROLE IF EXISTS bluetusk_md5_test;
            CREATE ROLE bluetusk_md5_test LOGIN PASSWORD 'md5-password';
            """);

        try
        {
            await using var session = await BlueTuskSession.OpenAsync(
                CreateOptions(settings, "bluetusk_md5_test", "md5-password"));
            var result = await session.ExecuteSimpleQueryAsync("SELECT current_user");

            Assert.Equal(
                "bluetusk_md5_test",
                Encoding.UTF8.GetString(
                    Assert.Single(Assert.Single(result.ResultSets).Rows).Values[0]!.Value.Span));
        }
        finally
        {
            await ExecuteAdminAsync(settings, "DROP ROLE IF EXISTS bluetusk_md5_test");
        }
    }

    [Fact]
    public async Task Cleartext_authentication_requires_plaintext_transport_opt_in()
    {
        var settings = GetSettings();
        await ExecuteAdminAsync(
            settings,
            """
            DROP ROLE IF EXISTS bluetusk_cleartext_test;
            CREATE ROLE bluetusk_cleartext_test LOGIN PASSWORD 'cleartext-password';
            """);

        try
        {
            var safeOptions = CreateOptions(
                settings,
                "bluetusk_cleartext_test",
                "cleartext-password");
            await Assert.ThrowsAsync<BlueTuskAuthenticationException>(
                () => BlueTuskSession.OpenAsync(safeOptions).AsTask());

            await using var session = await BlueTuskSession.OpenAsync(
                safeOptions with { AllowUnencryptedPassword = true });
            var result = await session.ExecuteSimpleQueryAsync("SELECT current_user");

            Assert.Equal(
                "bluetusk_cleartext_test",
                Encoding.UTF8.GetString(
                    Assert.Single(Assert.Single(result.ResultSets).Rows).Values[0]!.Value.Span));
        }
        finally
        {
            await ExecuteAdminAsync(settings, "DROP ROLE IF EXISTS bluetusk_cleartext_test");
        }
    }

    private static async Task ExecuteAdminAsync(
        BlueTuskConnectionStringBuilder settings,
        string sql)
    {
        await using var connection = new BlueTuskConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new BlueTuskCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static BlueTuskClientOptions CreateOptions(
        BlueTuskConnectionStringBuilder settings,
        string username,
        string password) => new()
        {
            Host = settings.Host,
            Port = settings.Port,
            Database = settings.Database,
            Username = username,
            Password = password,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };

    private static BlueTuskConnectionStringBuilder GetSettings()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
    }
}

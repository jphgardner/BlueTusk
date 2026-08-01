using System.Security.Cryptography.X509Certificates;
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

    [Fact]
    public async Task Password_files_and_callbacks_authenticate_against_SCRAM()
    {
        var settings = GetSettings();
        var password = settings.Password ?? throw SkipException.ForSkip(
            "The matrix connection string does not contain a password for credential-source testing.");
        var path = Path.Combine(Path.GetTempPath(), $"bluetusk-{Guid.NewGuid():N}.pgpass");
        await File.WriteAllTextAsync(
            path,
            $"{settings.Host}:{settings.Port}:{settings.Database}:{settings.Username}:{password}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        try
        {
            await using (var passfileSession = await BlueTuskSession.OpenAsync(
                             CreateOptions(settings, settings.Username, password: null) with
                             {
                                 Passfile = path,
                             }))
            {
                Assert.Equal(settings.Username, await ReadCurrentUserAsync(passfileSession));
            }

            var accessTokenCalls = 0;
            await using (var tokenSession = await BlueTuskSession.OpenAsync(
                             CreateOptions(settings, settings.Username, "wrong-explicit-value") with
                             {
                                 AccessTokenProviderAsync = (_, _) =>
                                 {
                                     accessTokenCalls++;
                                     return ValueTask.FromResult(password);
                                 },
                             }))
            {
                Assert.Equal(settings.Username, await ReadCurrentUserAsync(tokenSession));
            }

            Assert.Equal(1, accessTokenCalls);

            var callbackCalls = 0;
            var callbackSettings = new BlueTuskConnectionStringBuilder(settings.ConnectionString)
            {
                Password = null,
                Pooling = true,
                MinimumPoolSize = 0,
                MaximumPoolSize = 1,
            };
            await using var dataSource = new BlueTuskDataSourceBuilder(callbackSettings.ConnectionString)
                .UsePasswordProvider((_, _) =>
                {
                    callbackCalls++;
                    return ValueTask.FromResult(password);
                })
                .Build();

            await using (var connection = await dataSource.OpenConnectionAsync())
            {
                await using var command = new BlueTuskCommand("SELECT current_user", connection);
                Assert.Equal(settings.Username, await command.ExecuteScalarAsync());
            }

            await using (var connection = await dataSource.OpenConnectionAsync())
            {
                await using var command = new BlueTuskCommand("SELECT current_user", connection);
                Assert.Equal(settings.Username, await command.ExecuteScalarAsync());
            }

            Assert.Equal(1, callbackCalls);
            await dataSource.ClearPoolAsync();
            await using (var connection = await dataSource.OpenConnectionAsync())
            {
                await using var command = new BlueTuskCommand("SELECT current_user", connection);
                Assert.Equal(settings.Username, await command.ExecuteScalarAsync());
            }

            Assert.Equal(2, callbackCalls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Native_OAUTHBEARER_authenticates_sync_and_async_against_PostgreSQL_18()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_OAUTH_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_OAUTH_TEST_CONNECTION_STRING is not configured.");
        }

        var baseOptions = BlueTuskClientOptions.FromConnectionString(connectionString) with
        {
            Password = null,
            SslMode = BlueTuskSslMode.Require,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = static (_, certificate, _, _) =>
                certificate is not null &&
                string.Equals(certificate.Subject, "CN=localhost", StringComparison.Ordinal),
        };
        var syncCalls = 0;
        using (var session = BlueTuskSession.Open(
                   baseOptions with
                   {
                       AccessTokenProvider = _ =>
                       {
                           syncCalls++;
                           return "bluetusk-oauth-token";
                       },
                   }))
        {
            Assert.True(session.IsEncrypted);
            Assert.True(session.Capabilities.SupportsOAuthBearer);
            Assert.Equal("bluetusk_oauth_test", ReadCurrentUser(session));
        }

        var asyncCalls = 0;
        await using (var session = await BlueTuskSession.OpenAsync(
                         baseOptions with
                         {
                             AccessTokenProviderAsync = (_, _) =>
                             {
                                 asyncCalls++;
                                 return ValueTask.FromResult("bluetusk-oauth-token");
                             },
                         }))
        {
            Assert.True(session.IsEncrypted);
            Assert.True(session.Capabilities.SupportsOAuthBearer);
            Assert.Equal("bluetusk_oauth_test", await ReadCurrentUserAsync(session));
        }

        Assert.Equal(1, syncCalls);
        Assert.Equal(1, asyncCalls);

        var exception = await Assert.ThrowsAsync<BlueTuskServerException>(
            () => BlueTuskSession.OpenAsync(
                baseOptions with
                {
                    AccessTokenProviderAsync = (_, _) =>
                        ValueTask.FromResult("invalid-oauth-token"),
                }).AsTask());
        Assert.Equal("28000", exception.SqlState);
        Assert.DoesNotContain("invalid-oauth-token", exception.ToString(), StringComparison.Ordinal);
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
        string? password) => new()
        {
            Host = settings.Host,
            Port = settings.Port,
            Database = settings.Database,
            Username = username,
            Password = password,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };

    private static async Task<string> ReadCurrentUserAsync(BlueTuskSession session)
    {
        var result = await session.ExecuteSimpleQueryAsync("SELECT current_user");
        return Encoding.UTF8.GetString(
            Assert.Single(Assert.Single(result.ResultSets).Rows).Values[0]!.Value.Span);
    }

    private static string ReadCurrentUser(BlueTuskSession session)
    {
        var result = session.ExecuteSimpleQuery("SELECT current_user");
        return Encoding.UTF8.GetString(
            Assert.Single(Assert.Single(result.ResultSets).Rows).Values[0]!.Value.Span);
    }

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

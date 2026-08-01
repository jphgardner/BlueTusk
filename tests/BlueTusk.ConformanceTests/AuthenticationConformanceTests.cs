using System.Buffers.Binary;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Security;

namespace BlueTusk.ConformanceTests;

public sealed class AuthenticationConformanceTests
{
    private static readonly byte[] Md5Salt = [0x12, 0x34, 0x56, 0x78];

    [Fact]
    public async Task Authenticates_with_a_legacy_MD5_challenge_asynchronously()
    {
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(AuthenticationRequest(5, Md5Salt)),
            new FakeServerStep.ExpectFrontendMessage(
                (byte)'p',
                CStrings("md580cd925042851e77d703d2e1aba480ba")),
            new FakeServerStep.Send(StartupComplete()),
        ], CancellationToken.None);

        await using var session = await BlueTuskSession.OpenAsync(Options(server.Port));

        Assert.True(session.IsOpen);
        await serverTask;
    }

    [Fact]
    public async Task Authenticates_with_a_legacy_MD5_challenge_synchronously()
    {
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(AuthenticationRequest(5, Md5Salt)),
            new FakeServerStep.ExpectFrontendMessage(
                (byte)'p',
                CStrings("md580cd925042851e77d703d2e1aba480ba")),
            new FakeServerStep.Send(StartupComplete()),
        ], CancellationToken.None);

        using var session = BlueTuskSession.Open(Options(server.Port));

        Assert.True(session.IsOpen);
        await serverTask;
    }

    [Fact]
    public async Task Rejects_cleartext_password_authentication_over_plaintext_by_default()
    {
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(AuthenticationRequest(3)),
        ], CancellationToken.None);

        var exception = Assert.Throws<BlueTuskAuthenticationException>(
            () => BlueTuskSession.Open(Options(server.Port)));

        Assert.Contains("unencrypted connection", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pencil", exception.Message, StringComparison.Ordinal);
        await serverTask;
    }

    [Fact]
    public async Task Allows_explicit_cleartext_password_compatibility_asynchronously()
    {
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(AuthenticationRequest(3)),
            new FakeServerStep.ExpectFrontendMessage((byte)'p', CStrings("pencil")),
            new FakeServerStep.Send(StartupComplete()),
        ], CancellationToken.None);

        await using var session = await BlueTuskSession.OpenAsync(
            Options(server.Port) with { AllowUnencryptedPassword = true });

        Assert.True(session.IsOpen);
        await serverTask;
    }

    [Fact]
    public async Task Resolves_an_access_token_callback_for_each_async_physical_connection()
    {
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(AuthenticationRequest(3)),
            new FakeServerStep.ExpectFrontendMessage((byte)'p', CStrings("fresh-token")),
            new FakeServerStep.Send(StartupComplete()),
        ], CancellationToken.None);
        BlueTuskCredentialRequest? request = null;
        var calls = 0;

        await using var session = await BlueTuskSession.OpenAsync(
            Options(server.Port) with
            {
                AllowUnencryptedPassword = true,
                AccessTokenProviderAsync = (context, _) =>
                {
                    request = context;
                    calls++;
                    return ValueTask.FromResult("fresh-token");
                },
            });

        Assert.True(session.IsOpen);
        Assert.Equal(1, calls);
        Assert.Equal(
            new BlueTuskCredentialRequest("127.0.0.1", server.Port, "app", "user"),
            request);
        await serverTask;
    }

    [Fact]
    public async Task Resolves_a_password_callback_for_a_synchronous_connection()
    {
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(AuthenticationRequest(3)),
            new FakeServerStep.ExpectFrontendMessage((byte)'p', CStrings("callback-password")),
            new FakeServerStep.Send(StartupComplete()),
        ], CancellationToken.None);
        var calls = 0;

        using var session = BlueTuskSession.Open(
            Options(server.Port) with
            {
                AllowUnencryptedPassword = true,
                PasswordProvider = _ =>
                {
                    calls++;
                    return "callback-password";
                },
            });

        Assert.True(session.IsOpen);
        Assert.Equal(1, calls);
        await serverTask;
    }

    [Fact]
    public async Task Credential_callback_failures_do_not_expose_callback_payloads()
    {
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(AuthenticationRequest(3)),
        ], CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BlueTuskAuthenticationException>(
            () => BlueTuskSession.OpenAsync(
                Options(server.Port) with
                {
                    Password = null,
                    AccessTokenProviderAsync = (_, _) =>
                        throw new InvalidOperationException("token-value-must-not-escape"),
                }).AsTask());

        Assert.DoesNotContain("token-value-must-not-escape", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        await serverTask;
    }

    [Fact]
    public async Task Does_not_invoke_a_credential_callback_when_authentication_needs_no_password()
    {
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(StartupComplete()),
        ], CancellationToken.None);

        await using var session = await BlueTuskSession.OpenAsync(
            Options(server.Port) with
            {
                Password = null,
                PasswordProvider = _ => throw new InvalidOperationException("must not run"),
            });

        Assert.True(session.IsOpen);
        await serverTask;
    }

    [Fact]
    public async Task Rejects_OAUTHBEARER_without_TLS_before_resolving_a_token()
    {
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(AuthenticationRequest(10, CStrings("OAUTHBEARER", string.Empty))),
        ], CancellationToken.None);
        var calls = 0;

        var exception = await Assert.ThrowsAsync<BlueTuskAuthenticationException>(
            () => BlueTuskSession.OpenAsync(
                Options(server.Port) with
                {
                    Password = null,
                    AccessTokenProviderAsync = (_, _) =>
                    {
                        calls++;
                        return ValueTask.FromResult("must-not-be-resolved");
                    },
                }).AsTask());

        Assert.Contains("requires an encrypted TLS connection", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, calls);
        await serverTask;
    }

    [Fact]
    public async Task Resolves_a_matching_PostgreSQL_password_file_entry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bluetusk-{Guid.NewGuid():N}.pgpass");
        await File.WriteAllTextAsync(path, "127.0.0.1:*:app:user:passfile-password\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        try
        {
            await using var server = new FakePostgreSqlServer();
            var serverTask = server.RunAsync(
            [
                new FakeServerStep.ExpectFrontendMessage(Identifier: null),
                new FakeServerStep.Send(AuthenticationRequest(3)),
                new FakeServerStep.ExpectFrontendMessage((byte)'p', CStrings("passfile-password")),
                new FakeServerStep.Send(StartupComplete()),
            ], CancellationToken.None);

            await using var session = await BlueTuskSession.OpenAsync(
                Options(server.Port) with
                {
                    Password = null,
                    Passfile = path,
                    AllowUnencryptedPassword = true,
                });

            Assert.True(session.IsOpen);
            await serverTask;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Physical_replication_matches_the_special_password_file_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bluetusk-{Guid.NewGuid():N}.pgpass");
        await File.WriteAllTextAsync(
            path,
            "127.0.0.1:*:app:user:wrong-password\n" +
            "127.0.0.1:*:replication:user:replication-password\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        try
        {
            await using var server = new FakePostgreSqlServer();
            var serverTask = server.RunAsync(
            [
                new FakeServerStep.ExpectFrontendMessage(Identifier: null),
                new FakeServerStep.Send(AuthenticationRequest(3)),
                new FakeServerStep.ExpectFrontendMessage((byte)'p', CStrings("replication-password")),
                new FakeServerStep.Send(StartupComplete()),
            ], CancellationToken.None);

            await using var session = await BlueTuskSession.OpenAsync(
                Options(server.Port) with
                {
                    Password = null,
                    Passfile = path,
                    AllowUnencryptedPassword = true,
                    ReplicationMode = BlueTuskReplicationMode.Physical,
                });

            Assert.True(session.IsOpen);
            await serverTask;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static BlueTuskClientOptions Options(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Database = "app",
        Username = "user",
        Password = "pencil",
        SslMode = BlueTuskSslMode.Disable,
        ChannelBinding = BlueTuskChannelBindingMode.Disable,
    };

    private static byte[] AuthenticationRequest(int code, byte[]? data = null) =>
        Frame('R', Combine(Int32(code), data ?? []));

    private static byte[] StartupComplete() => Combine(
        Frame('R', Int32(0)),
        Frame('S', CStrings("server_version", "18.0")),
        Frame('K', Combine(Int32(123), Int32(456))),
        Frame('Z', [(byte)'I']));

    private static byte[] Frame(char identifier, byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = (byte)identifier;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + sizeof(int));
        payload.CopyTo(frame, 5);
        return frame;
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] CStrings(params string[] values) =>
        values.SelectMany(value => Encoding.UTF8.GetBytes(value).Append((byte)0)).ToArray();

    private static byte[] Combine(params byte[][] values)
    {
        var result = new byte[values.Sum(static value => value.Length)];
        var offset = 0;
        foreach (var value in values)
        {
            value.CopyTo(result, offset);
            offset += value.Length;
        }

        return result;
    }
}

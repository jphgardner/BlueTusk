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

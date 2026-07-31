using System.Buffers.Binary;
using System.Text;
using BlueTusk.Client;

namespace BlueTusk.ConformanceTests;

public sealed class PipelineConformanceTests
{
    [Fact]
    public async Task Pipeline_writes_explicit_sync_boundaries_and_drains_an_error_before_the_next_group()
    {
        await using var server = new FakePostgreSqlServer();
        var serverTask = server.RunAsync(
        [
            new FakeServerStep.ExpectFrontendMessage(Identifier: null),
            new FakeServerStep.Send(StartupResponse()),
            new FakeServerStep.ExpectFrontendMessage((byte)'P'),
            new FakeServerStep.ExpectFrontendMessage((byte)'B'),
            new FakeServerStep.ExpectFrontendMessage((byte)'D'),
            new FakeServerStep.ExpectFrontendMessage((byte)'E'),
            new FakeServerStep.ExpectFrontendMessage((byte)'S'),
            new FakeServerStep.ExpectFrontendMessage((byte)'P'),
            new FakeServerStep.ExpectFrontendMessage((byte)'B'),
            new FakeServerStep.ExpectFrontendMessage((byte)'D'),
            new FakeServerStep.ExpectFrontendMessage((byte)'E'),
            new FakeServerStep.ExpectFrontendMessage((byte)'S'),
            new FakeServerStep.Send(PipelineResponse()),
        ], CancellationToken.None);

        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Database = "app",
                Username = "app",
                Password = "password",
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            });

        var result = await session.ExecutePipelineAsync(
        [
            new BlueTuskPipelineGroup(
            [
                new BlueTuskBatchQuery("SELECT 1::int4 / 0::int4", [], UseBinaryResults: false),
            ]),
            new BlueTuskPipelineGroup(
            [
                new BlueTuskBatchQuery("UPDATE app.items SET value = 2", [], UseBinaryResults: false),
            ]),
        ]);

        Assert.Equal("22012", result.Groups[0].Error!.SqlState);
        Assert.True(result.Groups[1].Succeeded);
        Assert.Equal("UPDATE 1", Assert.Single(result.Groups[1].Result.ResultSets).CommandTag);
        Assert.Equal(BlueTusk.Protocol.BlueTuskTransactionStatus.Idle, session.TransactionStatus);
        Assert.Equal(new Version(19, 0), session.Capabilities.ServerVersion);
        Assert.True(session.Capabilities.SupportsPipelineMode);
        Assert.False(session.Capabilities.SupportsSqlPgq);
        await serverTask;
    }

    private static byte[] StartupResponse() => Combine(
        Frame('R', Int32(0)),
        Frame('S', CStrings("server_version", "19beta2")),
        Frame('K', Combine(Int32(123), Int32(456))),
        Frame('Z', [(byte)'I']));

    private static byte[] PipelineResponse() => Combine(
        Error("22012", "division by zero"),
        Frame('Z', [(byte)'I']),
        Frame('1', []),
        Frame('2', []),
        Frame('n', []),
        Frame('C', CStrings("UPDATE 1")),
        Frame('Z', [(byte)'I']));

    private static byte[] Error(string sqlState, string message) => Frame(
        'E',
        Combine(
            [(byte)'S'], CStrings("ERROR"),
            [(byte)'C'], CStrings(sqlState),
            [(byte)'M'], CStrings(message),
            [0]));

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

    private static byte[] CStrings(params string[] values) => Combine(
        values.SelectMany(value => Encoding.UTF8.GetBytes(value).Append((byte)0)).ToArray());

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

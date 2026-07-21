using System.Net;
using System.Net.Sockets;

namespace BlueTusk.ConformanceTests;

public sealed class FakePostgreSqlServerTests
{
    [Fact]
    public async Task Can_fragment_a_scripted_response_at_every_byte()
    {
        await using var server = new FakePostgreSqlServer();
        byte[] expected = [1, 2, 3, 4, 5];
        var serverTask = server.RunAsync(
            [new FakeServerStep.Send(expected, FragmentSize: 1), new FakeServerStep.Disconnect()],
            CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, CancellationToken.None);
        await using var stream = client.GetStream();
        var actual = new byte[expected.Length];
        await stream.ReadExactlyAsync(actual, CancellationToken.None);
        await serverTask;

        Assert.Equal(expected, actual);
    }
}

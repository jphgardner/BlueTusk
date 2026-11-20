using System.Net;
using System.Net.Sockets;
using BlueTusk.Transport;

namespace BlueTusk.Protocol.Tests;

public sealed class BlueTuskCancellationChannelTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Sends_cancel_request_on_a_dedicated_connection(bool asynchronous, bool reset)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receiveTask = ReceiveRequestAndCloseAsync(listener, reset);
        var endpoint = new BlueTuskEndpoint.Tcp("127.0.0.1", port);
        var options = new BlueTuskTransportOptions { ConnectTimeout = TimeSpan.FromSeconds(5) };
        var key = new BlueTuskBackendKeyData(123, 456);

        if (asynchronous)
        {
            await BlueTuskCancellationChannel.SendAsync(endpoint, options, key, CancellationToken.None);
        }
        else
        {
            BlueTuskCancellationChannel.Send(endpoint, options, key);
        }

        Assert.Equal("0000001004D2162E0000007B000001C8", await receiveTask);
    }

    private static async Task<string> ReceiveRequestAndCloseAsync(TcpListener listener, bool reset)
    {
        using var client = await listener.AcceptTcpClientAsync(CancellationToken.None);
        var request = new byte[sizeof(int) * 4];
        await client.GetStream().ReadExactlyAsync(request, CancellationToken.None);
        if (reset)
        {
            client.LingerState = new LingerOption(enable: true, seconds: 0);
        }

        return Convert.ToHexString(request);
    }
}

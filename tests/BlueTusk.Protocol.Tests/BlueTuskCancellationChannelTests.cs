using System.Net;
using System.Net.Sockets;
using BlueTusk.Transport;

namespace BlueTusk.Protocol.Tests;

public sealed class BlueTuskCancellationChannelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sends_cancel_request_on_a_dedicated_connection(bool asynchronous)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync(CancellationToken.None).AsTask();
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

        using var client = await acceptTask;
        var request = new byte[sizeof(int) * 4];
        await client.GetStream().ReadExactlyAsync(request, CancellationToken.None);
        Assert.Equal("0000001004D2162E0000007B000001C8", Convert.ToHexString(request));
    }
}

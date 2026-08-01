using System.Net;
using System.Net.Sockets;

namespace BlueTusk.Transport.Tests;

public sealed class BlueTuskSocketTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tcp_tries_every_resolved_address_in_order(bool asynchronous)
    {
        var addresses = new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Loopback };
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var transport = CreateTransport(addresses);

        if (asynchronous)
        {
            await transport.ConnectAsync(
                new BlueTuskEndpoint.Tcp("ordered.test", port),
                BlueTuskTransportOptions.Default,
                CancellationToken.None);
        }
        else
        {
            transport.Connect(
                new BlueTuskEndpoint.Tcp("ordered.test", port),
                BlueTuskTransportOptions.Default);
        }

        using var accepted = await listener.AcceptSocketAsync();
        var remoteEndpoint = Assert.IsType<IPEndPoint>(transport.RemoteEndPoint);
        Assert.Equal(IPAddress.Loopback, remoteEndpoint.Address);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tcp_reports_all_failed_address_attempts(bool asynchronous)
    {
        var addresses = new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Loopback };
        var port = ReserveUnusedPort();
        await using var transport = CreateTransport(addresses);
        var endpoint = new BlueTuskEndpoint.Tcp("unavailable.test", port);

        var exception = asynchronous
            ? await Assert.ThrowsAsync<BlueTuskTransportException>(
                () => transport.ConnectAsync(
                    endpoint,
                    BlueTuskTransportOptions.Default,
                    CancellationToken.None).AsTask())
            : Assert.Throws<BlueTuskTransportException>(
                () => transport.Connect(endpoint, BlueTuskTransportOptions.Default));

        Assert.Equal(BlueTuskTransportFailureKind.ConnectionRefused, exception.FailureKind);
        Assert.Equal(endpoint, exception.Endpoint);
        Assert.Equal(addresses, exception.AddressFailures.Select(static failure => failure.Address));
        Assert.All(
            exception.AddressFailures,
            static failure => Assert.Equal(SocketError.ConnectionRefused, failure.SocketErrorCode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dns_failures_have_a_stable_classification(bool asynchronous)
    {
        var socketException = new SocketException((int)SocketError.HostNotFound);
        await using var transport = new BlueTuskSocketTransport(
            _ => throw socketException,
            (_, _) => ValueTask.FromException<IPAddress[]>(socketException));
        var endpoint = new BlueTuskEndpoint.Tcp("missing.test");

        var exception = asynchronous
            ? await Assert.ThrowsAsync<BlueTuskTransportException>(
                () => transport.ConnectAsync(
                    endpoint,
                    BlueTuskTransportOptions.Default,
                    CancellationToken.None).AsTask())
            : Assert.Throws<BlueTuskTransportException>(
                () => transport.Connect(endpoint, BlueTuskTransportOptions.Default));

        Assert.Equal(BlueTuskTransportFailureKind.NameResolution, exception.FailureKind);
        Assert.Equal(endpoint, exception.Endpoint);
        Assert.Empty(exception.AddressFailures);
        Assert.Same(socketException, exception.InnerException);
    }

    [Fact]
    public async Task Async_dns_obeys_the_total_connection_timeout()
    {
        await using var transport = new BlueTuskSocketTransport(
            _ => [IPAddress.Loopback],
            ResolveNeverAsync);
        var endpoint = new BlueTuskEndpoint.Tcp("slow-dns.test");
        var options = new BlueTuskTransportOptions { ConnectTimeout = TimeSpan.FromMilliseconds(20) };

        var exception = await Assert.ThrowsAsync<BlueTuskTransportException>(
            () => transport.ConnectAsync(endpoint, options, CancellationToken.None).AsTask());

        Assert.Equal(BlueTuskTransportFailureKind.Timeout, exception.FailureKind);
        Assert.Equal(endpoint, exception.Endpoint);
        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reclassified_as_a_connection_failure()
    {
        await using var transport = new BlueTuskSocketTransport(
            _ => [IPAddress.Loopback],
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult<IPAddress[]>([IPAddress.Loopback]);
            });
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.ConnectAsync(
                new BlueTuskEndpoint.Tcp("cancelled.test"),
                BlueTuskTransportOptions.Default,
                cancellationSource.Token).AsTask());
    }

    [Fact]
    public async Task Tcp_socket_options_are_applied_before_connect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var options = new BlueTuskTransportOptions
        {
            KeepAlive = true,
            NoDelay = true,
            ReceiveBufferSize = 48 * 1024,
            SendBufferSize = 40 * 1024,
        };
        await using var transport = CreateTransport([IPAddress.Loopback]);

        await transport.ConnectAsync(
            new BlueTuskEndpoint.Tcp("options.test", port),
            options,
            CancellationToken.None);
        using var accepted = await listener.AcceptSocketAsync();

        var socket = Assert.IsType<Socket>(transport.ConnectedSocket);
        Assert.True(socket.NoDelay);
        Assert.NotEqual(
            0,
            Convert.ToInt32(
                socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive),
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(socket.ReceiveBufferSize >= options.ReceiveBufferSize);
        Assert.True(socket.SendBufferSize >= options.SendBufferSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unix_domain_sockets_support_sync_and_async_connections(bool asynchronous)
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"bt-{Guid.NewGuid():N}.sock");
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            await using var transport = new BlueTuskSocketTransport();
            var endpoint = new BlueTuskEndpoint.UnixSocket(path);

            if (asynchronous)
            {
                await transport.ConnectAsync(endpoint, BlueTuskTransportOptions.Default, CancellationToken.None);
            }
            else
            {
                transport.Connect(endpoint, BlueTuskTransportOptions.Default);
            }

            using var accepted = await listener.AcceptAsync();
            Assert.IsType<UnixDomainSocketEndPoint>(transport.RemoteEndPoint);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(SocketError.TimedOut, BlueTuskTransportFailureKind.Timeout)]
    [InlineData(SocketError.NetworkUnreachable, BlueTuskTransportFailureKind.NetworkUnreachable)]
    [InlineData(SocketError.HostUnreachable, BlueTuskTransportFailureKind.HostUnreachable)]
    [InlineData(SocketError.AddressNotAvailable, BlueTuskTransportFailureKind.AddressUnavailable)]
    [InlineData(SocketError.ConnectionAborted, BlueTuskTransportFailureKind.SocketFailure)]
    public void Socket_errors_are_classified(
        SocketError socketError,
        BlueTuskTransportFailureKind expected)
    {
        var endpoint = new BlueTuskEndpoint.Tcp("classification.test");

        var exception = BlueTuskTransportException.ForSocket(
            endpoint,
            [],
            new SocketException((int)socketError));

        Assert.Equal(expected, exception.FailureKind);
    }

    private static BlueTuskSocketTransport CreateTransport(IPAddress[] addresses) =>
        new(
            _ => addresses,
            (_, _) => ValueTask.FromResult(addresses));

    private static int ReserveUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async ValueTask<IPAddress[]> ResolveNeverAsync(
        string _,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return [];
    }
}

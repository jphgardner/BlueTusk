using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace BlueTusk.Transport;

/// <summary>A socket-backed transport with genuine asynchronous I/O.</summary>
public sealed class BlueTuskSocketTransport : IBlueTuskTransport
{
    private Socket? _socket;
    private NetworkStream? _stream;
    private bool _disposed;

    public EndPoint? RemoteEndPoint => _socket?.RemoteEndPoint;

    public async ValueTask ConnectAsync(
        BlueTuskEndpoint endpoint,
        BlueTuskTransportOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (_socket is not null)
        {
            throw new InvalidOperationException("The transport is already connected.");
        }

        using var timeoutSource = options.ConnectTimeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(options.ConnectTimeout);
        using var linkedSource = timeoutSource is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var effectiveToken = linkedSource?.Token ?? cancellationToken;

        try
        {
            var socket = endpoint switch
            {
                BlueTuskEndpoint.Tcp => new Socket(SocketType.Stream, ProtocolType.Tcp),
                BlueTuskEndpoint.UnixSocket => new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified),
                _ => throw new NotSupportedException($"Endpoint type '{endpoint.GetType().Name}' is not supported."),
            };

            _socket = socket;
            socket.NoDelay = options.NoDelay;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, options.KeepAlive);
            socket.ReceiveBufferSize = options.ReceiveBufferSize;
            socket.SendBufferSize = options.SendBufferSize;

            EndPoint socketEndpoint = endpoint switch
            {
                BlueTuskEndpoint.Tcp tcp => new DnsEndPoint(tcp.Host, tcp.Port),
                BlueTuskEndpoint.UnixSocket unix => new UnixDomainSocketEndPoint(unix.Path),
                _ => throw new UnreachableException(),
            };

            await socket.ConnectAsync(socketEndpoint, effectiveToken).ConfigureAwait(false);
            _stream = new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException exception) when (
            timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            DisposeSocket();
            throw new TimeoutException($"Connecting to PostgreSQL exceeded the {options.ConnectTimeout} timeout.", exception);
        }
        catch
        {
            DisposeSocket();
            throw;
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        GetConnectedStream().ReadAsync(buffer, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        GetConnectedStream().WriteAsync(buffer, cancellationToken);

    public ValueTask FlushAsync(CancellationToken cancellationToken) =>
        new(GetConnectedStream().FlushAsync(cancellationToken));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeSocket();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private NetworkStream GetConnectedStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _stream ?? throw new InvalidOperationException("The transport is not connected.");
    }

    private void DisposeSocket()
    {
        _stream?.Dispose();
        _stream = null;
        _socket?.Dispose();
        _socket = null;
    }
}

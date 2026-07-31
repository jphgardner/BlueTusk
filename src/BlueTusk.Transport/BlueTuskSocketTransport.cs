using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace BlueTusk.Transport;

/// <summary>A socket-backed transport with genuine asynchronous I/O.</summary>
public sealed class BlueTuskSocketTransport : IBlueTuskTlsTransport
{
    private Socket? _socket;
    private Stream? _stream;
    private X509Certificate2? _remoteCertificate;
    private bool _disposed;

    public EndPoint? RemoteEndPoint => _socket?.RemoteEndPoint;

    public bool IsEncrypted => _stream is SslStream;

    public X509Certificate2? RemoteCertificate => _remoteCertificate;

    public void Connect(BlueTuskEndpoint endpoint, BlueTuskTransportOptions options)
    {
        ValidateConnect(endpoint, options);
        var started = Stopwatch.GetTimestamp();
        try
        {
            _socket = endpoint switch
            {
                BlueTuskEndpoint.Tcp tcp => ConnectTcp(tcp, options, started),
                BlueTuskEndpoint.UnixSocket unix => ConnectSocket(
                    new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified),
                    new UnixDomainSocketEndPoint(unix.Path),
                    options,
                    started),
                _ => throw new NotSupportedException($"Endpoint type '{endpoint.GetType().Name}' is not supported."),
            };
            _stream = new NetworkStream(_socket, ownsSocket: true);
        }
        catch
        {
            DisposeSocket();
            throw;
        }
    }

    public async ValueTask ConnectAsync(
        BlueTuskEndpoint endpoint,
        BlueTuskTransportOptions options,
        CancellationToken cancellationToken)
    {
        ValidateConnect(endpoint, options);

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

    public int Read(Span<byte> buffer) => GetConnectedStream().Read(buffer);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        GetConnectedStream().WriteAsync(buffer, cancellationToken);

    public void Write(ReadOnlySpan<byte> buffer) => GetConnectedStream().Write(buffer);

    public ValueTask FlushAsync(CancellationToken cancellationToken) =>
        new(GetConnectedStream().FlushAsync(cancellationToken));

    public void Flush() => GetConnectedStream().Flush();

    public async ValueTask UpgradeToTlsAsync(BlueTuskTlsOptions options, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var innerStream = GetConnectedStream();
        if (innerStream is SslStream)
        {
            throw new InvalidOperationException("The transport is already encrypted.");
        }

        X509CertificateCollection? certificates = null;
        if (options.ClientCertificates.Count != 0)
        {
            certificates = new X509CertificateCollection();
            certificates.AddRange(options.ClientCertificates.ToArray());
        }
        var tlsStream = new SslStream(
            innerStream,
            leaveInnerStreamOpen: false,
            options.RemoteCertificateValidationCallback);

        try
        {
            await tlsStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = options.TargetHost,
                    EnabledSslProtocols = options.EnabledProtocols,
                    CertificateRevocationCheckMode = options.CertificateRevocationCheckMode,
                    ClientCertificates = certificates,
                },
                cancellationToken).ConfigureAwait(false);

            _stream = tlsStream;
            _remoteCertificate = tlsStream.RemoteCertificate is { } certificate
                ? new X509Certificate2(certificate)
                : null;
        }
        catch
        {
            await tlsStream.DisposeAsync().ConfigureAwait(false);
            DisposeSocket();
            throw;
        }
    }

    public void UpgradeToTls(BlueTuskTlsOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var innerStream = GetConnectedStream();
        if (innerStream is SslStream)
        {
            throw new InvalidOperationException("The transport is already encrypted.");
        }

        X509CertificateCollection? certificates = null;
        if (options.ClientCertificates.Count != 0)
        {
            certificates = new X509CertificateCollection();
            certificates.AddRange(options.ClientCertificates.ToArray());
        }
        var tlsStream = new SslStream(
            innerStream,
            leaveInnerStreamOpen: false,
            options.RemoteCertificateValidationCallback);
        try
        {
            tlsStream.AuthenticateAsClient(
                new SslClientAuthenticationOptions
                {
                    TargetHost = options.TargetHost,
                    EnabledSslProtocols = options.EnabledProtocols,
                    CertificateRevocationCheckMode = options.CertificateRevocationCheckMode,
                    ClientCertificates = certificates,
                });
            _stream = tlsStream;
            _remoteCertificate = tlsStream.RemoteCertificate is { } certificate
                ? new X509Certificate2(certificate)
                : null;
        }
        catch
        {
            tlsStream.Dispose();
            DisposeSocket();
            throw;
        }
    }

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

    private Stream GetConnectedStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _stream ?? throw new InvalidOperationException("The transport is not connected.");
    }

    private static Socket ConnectTcp(
        BlueTuskEndpoint.Tcp endpoint,
        BlueTuskTransportOptions options,
        long started)
    {
        var addresses = Dns.GetHostAddresses(endpoint.Host)
            .OrderBy(static address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();
        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                return ConnectSocket(socket, new IPEndPoint(address, endpoint.Port), options, started);
            }
            catch (SocketException exception)
            {
                lastError = exception;
                socket.Dispose();
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static Socket ConnectSocket(
        Socket socket,
        EndPoint endpoint,
        BlueTuskTransportOptions options,
        long started)
    {
        ConfigureSocket(socket, options);
        socket.Blocking = false;
        try
        {
            try
            {
                socket.Connect(endpoint);
            }
            catch (SocketException exception) when (
                exception.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress or SocketError.AlreadyInProgress)
            {
                WaitForConnect(socket, options.ConnectTimeout, started);
            }

            return socket;
        }
        finally
        {
            socket.Blocking = true;
        }
    }

    private static void WaitForConnect(Socket socket, TimeSpan timeout, long started)
    {
        while (true)
        {
            var pollInterval = TimeSpan.FromSeconds(1);
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Connecting to PostgreSQL exceeded the {timeout} timeout.");
                }

                pollInterval = remaining < pollInterval ? remaining : pollInterval;
            }

            var microseconds = Math.Max(1, checked((int)Math.Ceiling(pollInterval.TotalMicroseconds)));
            if (!socket.Poll(microseconds, SelectMode.SelectWrite) &&
                !socket.Poll(0, SelectMode.SelectError))
            {
                continue;
            }

            var error = (SocketError)Convert.ToInt32(
                socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error),
                System.Globalization.CultureInfo.InvariantCulture);
            if (error != SocketError.Success)
            {
                throw new SocketException((int)error);
            }

            return;
        }
    }

    private static void ConfigureSocket(Socket socket, BlueTuskTransportOptions options)
    {
        socket.NoDelay = options.NoDelay;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, options.KeepAlive);
        socket.ReceiveBufferSize = options.ReceiveBufferSize;
        socket.SendBufferSize = options.SendBufferSize;
    }

    private void ValidateConnect(BlueTuskEndpoint endpoint, BlueTuskTransportOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (_socket is not null)
        {
            throw new InvalidOperationException("The transport is already connected.");
        }
    }

    private void DisposeSocket()
    {
        _stream?.Dispose();
        _stream = null;
        _remoteCertificate?.Dispose();
        _remoteCertificate = null;
        _socket?.Dispose();
        _socket = null;
    }
}

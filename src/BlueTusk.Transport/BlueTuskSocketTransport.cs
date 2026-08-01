using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace BlueTusk.Transport;

/// <summary>A socket-backed transport with genuine asynchronous I/O.</summary>
public sealed class BlueTuskSocketTransport : IBlueTuskTlsTransport
{
    private readonly Func<string, IPAddress[]> _resolveAddresses;
    private readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> _resolveAddressesAsync;
    private Socket? _socket;
    private Stream? _stream;
    private X509Certificate2? _remoteCertificate;
    private bool _disposed;

    public BlueTuskSocketTransport()
        : this(
            Dns.GetHostAddresses,
            static (host, cancellationToken) =>
                new ValueTask<IPAddress[]>(Dns.GetHostAddressesAsync(host, cancellationToken)))
    {
    }

    internal BlueTuskSocketTransport(
        Func<string, IPAddress[]> resolveAddresses,
        Func<string, CancellationToken, ValueTask<IPAddress[]>> resolveAddressesAsync)
    {
        _resolveAddresses = resolveAddresses ?? throw new ArgumentNullException(nameof(resolveAddresses));
        _resolveAddressesAsync = resolveAddressesAsync ?? throw new ArgumentNullException(nameof(resolveAddressesAsync));
    }

    public EndPoint? RemoteEndPoint => _socket?.RemoteEndPoint;

    public bool IsEncrypted => _stream is SslStream;

    public X509Certificate2? RemoteCertificate => _remoteCertificate;

    internal Socket? ConnectedSocket => _socket;

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
        catch (BlueTuskTransportException)
        {
            DisposeSocket();
            throw;
        }
        catch (TimeoutException exception)
        {
            DisposeSocket();
            throw BlueTuskTransportException.ForTimeout(endpoint, options.ConnectTimeout, exception);
        }
        catch (SocketException exception)
        {
            DisposeSocket();
            throw BlueTuskTransportException.ForSocket(endpoint, [], exception);
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
            _socket = endpoint switch
            {
                BlueTuskEndpoint.Tcp tcp => await ConnectTcpAsync(tcp, options, effectiveToken).ConfigureAwait(false),
                BlueTuskEndpoint.UnixSocket unix => await ConnectUnixSocketAsync(unix, options, effectiveToken)
                    .ConfigureAwait(false),
                _ => throw new NotSupportedException($"Endpoint type '{endpoint.GetType().Name}' is not supported."),
            };
            _stream = new NetworkStream(_socket, ownsSocket: true);
        }
        catch (OperationCanceledException exception) when (
            timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            DisposeSocket();
            throw BlueTuskTransportException.ForTimeout(endpoint, options.ConnectTimeout, exception);
        }
        catch (BlueTuskTransportException)
        {
            DisposeSocket();
            throw;
        }
        catch (SocketException exception)
        {
            DisposeSocket();
            throw BlueTuskTransportException.ForSocket(endpoint, [], exception);
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
                    LocalCertificateSelectionCallback = options.LocalCertificateSelectionCallback,
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
                    LocalCertificateSelectionCallback = options.LocalCertificateSelectionCallback,
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

    private Socket ConnectTcp(
        BlueTuskEndpoint.Tcp endpoint,
        BlueTuskTransportOptions options,
        long started)
    {
        IPAddress[] addresses;
        try
        {
            addresses = OrderAddresses(_resolveAddresses(endpoint.Host));
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and not OutOfMemoryException)
        {
            throw BlueTuskTransportException.ForNameResolution(endpoint, exception);
        }

        if (addresses.Length == 0)
        {
            throw BlueTuskTransportException.ForNameResolution(
                endpoint,
                new SocketException((int)SocketError.HostNotFound));
        }

        var failures = new List<BlueTuskAddressFailure>(addresses.Length);
        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                return ConnectSocket(socket, new IPEndPoint(address, endpoint.Port), options, started);
            }
            catch (SocketException exception)
            {
                lastError = exception;
                failures.Add(new BlueTuskAddressFailure(address, exception.SocketErrorCode));
                socket?.Dispose();
            }
            catch
            {
                socket?.Dispose();
                throw;
            }
        }

        throw BlueTuskTransportException.ForSocket(
            endpoint,
            failures,
            lastError ?? new SocketException((int)SocketError.HostNotFound));
    }

    private async ValueTask<Socket> ConnectTcpAsync(
        BlueTuskEndpoint.Tcp endpoint,
        BlueTuskTransportOptions options,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = OrderAddresses(
                await _resolveAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and not OutOfMemoryException)
        {
            throw BlueTuskTransportException.ForNameResolution(endpoint, exception);
        }

        if (addresses.Length == 0)
        {
            throw BlueTuskTransportException.ForNameResolution(
                endpoint,
                new SocketException((int)SocketError.HostNotFound));
        }

        var failures = new List<BlueTuskAddressFailure>(addresses.Length);
        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                ConfigureSocket(socket, options);
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return socket;
            }
            catch (SocketException exception)
            {
                lastError = exception;
                failures.Add(new BlueTuskAddressFailure(address, exception.SocketErrorCode));
                socket?.Dispose();
            }
            catch
            {
                socket?.Dispose();
                throw;
            }
        }

        throw BlueTuskTransportException.ForSocket(
            endpoint,
            failures,
            lastError ?? new SocketException((int)SocketError.HostNotFound));
    }

    private static async ValueTask<Socket> ConnectUnixSocketAsync(
        BlueTuskEndpoint.UnixSocket endpoint,
        BlueTuskTransportOptions options,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        ConfigureSocket(socket, options);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Path), cancellationToken)
                .ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static IPAddress[] OrderAddresses(IEnumerable<IPAddress> addresses) =>
        addresses
            .OrderBy(static address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

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
        if (socket.ProtocolType == ProtocolType.Tcp)
        {
            socket.NoDelay = options.NoDelay;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, options.KeepAlive);
        }

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

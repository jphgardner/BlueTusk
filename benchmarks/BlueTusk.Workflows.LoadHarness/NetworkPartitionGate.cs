using System.Net;
using System.Net.Sockets;

namespace BlueTusk.Workflows.LoadHarness;

internal sealed class NetworkPartitionGate : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly List<Task> _sessions = [];
    private readonly HashSet<(TcpClient Client, TcpClient Server)> _active = [];
    private readonly Lock _gate = new();
    private readonly string _serverHost;
    private readonly int _serverPort;
    private readonly Task _accepting;
    private bool _partitioned;
    private int _rejected;
    private int _cut;
    private int _peakActive;

    internal NetworkPartitionGate(string host, int port)
    {
        _serverHost = host;
        _serverPort = port;
        _listener.Start(8);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync(_stop.Token);
    }

    internal int Port { get; }
    internal int RejectedConnections => Volatile.Read(ref _rejected);
    internal int PeakActiveConnections { get { lock (_gate) { return _peakActive; } } }
    internal int CutConnections => Volatile.Read(ref _cut);

    internal void Partition(bool enabled)
    {
        lock (_gate)
        {
            _partitioned = enabled;
            if (!enabled) { return; }
            foreach (var pair in _active)
            {
                Interlocked.Increment(ref _cut);
                pair.Client.Close();
                pair.Server.Close();
            }
        }
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (int count = 0; count < 128; count++)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                bool reject;
                lock (_gate) { reject = _partitioned || _active.Count >= 8; }
                if (reject)
                {
                    Interlocked.Increment(ref _rejected);
                    client.Dispose();
                    continue;
                }

                _sessions.Add(RelayAsync(client, cancellationToken));
            }

            Program.Check(false, "partition gate session budget");
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is OperationCanceledException or SocketException)
        {
            // Structured scenario shutdown.
        }
    }

    private async Task RelayAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var server = new TcpClient())
        {
            try
            {
                await server.ConnectAsync(_serverHost, _serverPort, cancellationToken);
                lock (_gate)
                {
                    if (_partitioned || _active.Count >= 8)
                    {
                        Interlocked.Increment(ref _rejected);
                        return;
                    }

                    _active.Add((client, server));
                    _peakActive = Math.Max(_peakActive, _active.Count);
                }

                using var clientStream = client.GetStream();
                using var serverStream = server.GetStream();
                using var pumpsStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task frontend = clientStream.CopyToAsync(serverStream, 8192, pumpsStop.Token);
                Task backend = serverStream.CopyToAsync(clientStream, 8192, pumpsStop.Token);
                _ = await Task.WhenAny(frontend, backend);
                await pumpsStop.CancelAsync();
                client.Close();
                server.Close();
                await Task.WhenAll(frontend, backend);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The injected partition closes established connections and rejects attempted replacements.
            }
            finally
            {
                lock (_gate) { _active.Remove((client, server)); }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Partition(true);
        await _accepting;
        await Task.WhenAll(_sessions);
        _stop.Dispose();
    }
}

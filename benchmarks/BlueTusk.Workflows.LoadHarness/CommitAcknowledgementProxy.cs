using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace BlueTusk.Workflows.LoadHarness;

/// <summary>A bounded loopback fault injector; never logs protocol bodies or authentication data.</summary>
internal sealed class CommitAcknowledgementProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly Task _forwarding;
    private int _dropped;

    internal CommitAcknowledgementProxy(string serverHost, int serverPort)
    {
        _listener.Start(1);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _forwarding = ForwardAsync(serverHost, serverPort, _stop.Token);
    }

    internal int Port { get; }
    internal bool DroppedCommitAcknowledgement => Volatile.Read(ref _dropped) == 1;

    private async Task ForwardAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            // One faulted session and one healthy replacement session; never an unbounded proxy service.
            for (int session = 0; session < 2; session++)
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                using var server = new TcpClient();
                await server.ConnectAsync(host, port, cancellationToken);
                using var clientStream = client.GetStream();
                using var serverStream = server.GetStream();
                byte[] length = new byte[4];
                await clientStream.ReadExactlyAsync(length, cancellationToken);
                int startupLength = BinaryPrimitives.ReadInt32BigEndian(length);
                Program.Check(startupLength is >= 8 and <= 1048576, "bounded proxy startup frame");
                byte[] startup = new byte[startupLength - 4];
                await clientStream.ReadExactlyAsync(startup, cancellationToken);
                Program.Check(BinaryPrimitives.ReadInt32BigEndian(startup) == 196608, "fault proxy requires plaintext PostgreSQL startup");
                await serverStream.WriteAsync(length, cancellationToken);
                await serverStream.WriteAsync(startup, cancellationToken);
                using var pumpsStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task frontend = PumpAsync(clientStream, serverStream, inspectCommit: false, pumpsStop.Token);
                Task backend = PumpAsync(serverStream, clientStream, inspectCommit: session == 0, pumpsStop.Token);
                _ = await Task.WhenAny(frontend, backend);
                await pumpsStop.CancelAsync();
                client.Close();
                server.Close();
                try
                {
                    await Task.WhenAll(frontend, backend);
                }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // One closed session must not prevent accepting its healthy pool replacement.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Connection loss and pump cancellation are the deliberate injected fault.
        }
    }

    private async Task PumpAsync(Stream source, Stream destination, bool inspectCommit, CancellationToken cancellationToken)
    {
        byte[] header = new byte[5];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await source.ReadAsync(header.AsMemory(0, 1), cancellationToken);
            if (read == 0) { return; }
            await source.ReadExactlyAsync(header.AsMemory(1, 4), cancellationToken);
            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            Program.Check(length is >= 4 and <= 1048576, "bounded proxy protocol frame");
            byte[] body = new byte[length - 4];
            await source.ReadExactlyAsync(body, cancellationToken);
            if (inspectCommit && header[0] == (byte)'C' && body.AsSpan().SequenceEqual("COMMIT\0"u8))
            {
                // PostgreSQL emits CommandComplete only after COMMIT has executed. Suppress that acknowledgement.
                Volatile.Write(ref _dropped, 1);
                return;
            }

            await destination.WriteAsync(header, cancellationToken);
            await destination.WriteAsync(body, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _forwarding;
        _stop.Dispose();
    }
}

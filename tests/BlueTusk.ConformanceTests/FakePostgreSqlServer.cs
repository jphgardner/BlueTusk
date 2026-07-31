using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace BlueTusk.ConformanceTests;

internal sealed class FakePostgreSqlServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private bool _disposed;

    public FakePostgreSqlServer()
    {
        _listener.Start(backlog: 1);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public async Task RunAsync(IEnumerable<FakeServerStep> script, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(script);

        using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        foreach (var step in script)
        {
            switch (step)
            {
                case FakeServerStep.Send send:
                    var fragmentSize = send.FragmentSize ?? send.Bytes.Length;
                    if (fragmentSize <= 0)
                    {
                        throw new InvalidOperationException("A fake-server fragment size must be positive.");
                    }

                    for (var offset = 0; offset < send.Bytes.Length; offset += fragmentSize)
                    {
                        var count = Math.Min(fragmentSize, send.Bytes.Length - offset);
                        await stream.WriteAsync(send.Bytes.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case FakeServerStep.Delay delay:
                    await Task.Delay(delay.Duration, cancellationToken).ConfigureAwait(false);
                    break;
                case FakeServerStep.ExpectFrontendMessage expect:
                    await ExpectFrontendMessageAsync(stream, expect.Identifier, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case FakeServerStep.Disconnect:
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(script), step, "Unknown fake server step.");
            }
        }
    }

    private static async Task ExpectFrontendMessageAsync(
        NetworkStream stream,
        byte? expectedIdentifier,
        CancellationToken cancellationToken)
    {
        var header = new byte[expectedIdentifier is null ? 4 : 5];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var lengthOffset = 0;
        if (expectedIdentifier is { } identifier)
        {
            if (header[0] != identifier)
            {
                throw new InvalidOperationException(
                    $"Expected frontend message '{(char)identifier}', received '{(char)header[0]}'.");
            }

            lengthOffset = 1;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(lengthOffset, sizeof(int)));
        if (length < sizeof(int))
        {
            throw new InvalidOperationException($"Frontend message declared invalid length {length}.");
        }

        var payloadLength = length - sizeof(int);
        if (payloadLength > 0)
        {
            var payload = new byte[payloadLength];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _listener.Stop();
        }

        return ValueTask.CompletedTask;
    }
}

internal abstract record FakeServerStep
{
    private FakeServerStep()
    {
    }

    internal sealed record Send(byte[] Bytes, int? FragmentSize = null) : FakeServerStep;

    internal sealed record Delay(TimeSpan Duration) : FakeServerStep;

    internal sealed record ExpectFrontendMessage(byte? Identifier) : FakeServerStep;

    internal sealed record Disconnect : FakeServerStep;
}

using System.Net;

namespace BlueTusk.Transport;

/// <summary>Moves bytes between BlueTusk and a PostgreSQL endpoint.</summary>
public interface IBlueTuskTransport : IAsyncDisposable, IDisposable
{
    EndPoint? RemoteEndPoint { get; }

    ValueTask ConnectAsync(
        BlueTuskEndpoint endpoint,
        BlueTuskTransportOptions options,
        CancellationToken cancellationToken);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    ValueTask FlushAsync(CancellationToken cancellationToken);
}


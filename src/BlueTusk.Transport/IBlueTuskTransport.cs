using System.Net;

namespace BlueTusk.Transport;

/// <summary>Moves bytes between BlueTusk and a PostgreSQL endpoint.</summary>
public interface IBlueTuskTransport : IAsyncDisposable, IDisposable
{
    EndPoint? RemoteEndPoint { get; }

    void Connect(BlueTuskEndpoint endpoint, BlueTuskTransportOptions options);

    ValueTask ConnectAsync(
        BlueTuskEndpoint endpoint,
        BlueTuskTransportOptions options,
        CancellationToken cancellationToken);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    int Read(Span<byte> buffer);

    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    void Write(ReadOnlySpan<byte> buffer);

    ValueTask FlushAsync(CancellationToken cancellationToken);

    void Flush();
}

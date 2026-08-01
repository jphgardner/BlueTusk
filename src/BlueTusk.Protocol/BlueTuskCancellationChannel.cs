using System.Buffers;
using BlueTusk.Transport;

namespace BlueTusk.Protocol;

/// <summary>Sends PostgreSQL cancellation requests over their required dedicated connection.</summary>
public static class BlueTuskCancellationChannel
{
    public static void Send(
        BlueTuskEndpoint endpoint,
        BlueTuskTransportOptions transportOptions,
        BlueTuskBackendKeyData backendKeyData)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(transportOptions);
        var output = CreateRequest(backendKeyData);
        using var transport = new BlueTuskSocketTransport();
        transport.Connect(endpoint, transportOptions);
        transport.Write(output.WrittenSpan);
        transport.Flush();
        WaitForServerClose(transport);
    }

    public static async ValueTask SendAsync(
        BlueTuskEndpoint endpoint,
        BlueTuskTransportOptions transportOptions,
        BlueTuskBackendKeyData backendKeyData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(transportOptions);
        var output = CreateRequest(backendKeyData);
        await using var transport = new BlueTuskSocketTransport();
        await transport.ConnectAsync(endpoint, transportOptions, cancellationToken).ConfigureAwait(false);
        await transport.WriteAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await transport.FlushAsync(cancellationToken).ConfigureAwait(false);
        await WaitForServerCloseAsync(transport, cancellationToken).ConfigureAwait(false);
    }

    private static void WaitForServerClose(BlueTuskSocketTransport transport)
    {
        Span<byte> response = stackalloc byte[1];
        while (transport.Read(response) != 0)
        {
            // PostgreSQL sends no response and closes the one-shot cancellation connection.
        }
    }

    private static async ValueTask WaitForServerCloseAsync(
        BlueTuskSocketTransport transport,
        CancellationToken cancellationToken)
    {
        var response = new byte[1];
        while (await transport.ReadAsync(response, cancellationToken).ConfigureAwait(false) != 0)
        {
            // PostgreSQL sends no response and closes the one-shot cancellation connection.
        }
    }

    private static ArrayBufferWriter<byte> CreateRequest(BlueTuskBackendKeyData backendKeyData)
    {
        var output = new ArrayBufferWriter<byte>(sizeof(int) * 4);
        BlueTuskFrontendMessageWriter.WriteCancelRequest(output, backendKeyData);
        return output;
    }
}

namespace BlueTusk.Transport;

/// <summary>Low-level socket options. TLS is negotiated by the protocol layer.</summary>
public sealed record BlueTuskTransportOptions
{
    public static BlueTuskTransportOptions Default { get; } = new();

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public bool NoDelay { get; init; } = true;

    public bool KeepAlive { get; init; } = true;

    public int ReceiveBufferSize { get; init; } = 256 * 1024;

    public int SendBufferSize { get; init; } = 32 * 1024;

    internal void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero && ConnectTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        }

        if (ReceiveBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ReceiveBufferSize));
        }

        if (SendBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SendBufferSize));
        }
    }
}

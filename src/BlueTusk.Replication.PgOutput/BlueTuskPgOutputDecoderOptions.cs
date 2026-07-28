namespace BlueTusk.Replication.PgOutput;

public enum BlueTuskPgOutputStreamingMode
{
    Off,
    On,
    Parallel,
}

/// <summary>Negotiated pgoutput protocol capabilities used while decoding.</summary>
public sealed record BlueTuskPgOutputDecoderOptions
{
    public int ProtocolVersion { get; init; } = 1;

    public BlueTuskPgOutputStreamingMode StreamingMode { get; init; }

    public bool TwoPhase { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ProtocolVersion, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ProtocolVersion, 4);
        if (!Enum.IsDefined(StreamingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(StreamingMode));
        }

        if (StreamingMode != BlueTuskPgOutputStreamingMode.Off && ProtocolVersion < 2)
        {
            throw new ArgumentException(
                "Transaction streaming requires pgoutput protocol version 2 or later.");
        }

        if (StreamingMode == BlueTuskPgOutputStreamingMode.Parallel && ProtocolVersion < 4)
        {
            throw new ArgumentException(
                "Parallel transaction streaming requires pgoutput protocol version 4.");
        }

        if (TwoPhase && ProtocolVersion < 3)
        {
            throw new ArgumentException(
                "Two-phase decoding requires pgoutput protocol version 3 or later.");
        }
    }
}

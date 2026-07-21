namespace BlueTusk.Protocol.Capture;

public enum BlueTuskCaptureDirection : byte
{
    Frontend = 0,
    Backend = 1,
}

[Flags]
public enum BlueTuskCaptureRecordAttributes : byte
{
    None = 0,
    Redacted = 1,
    Encrypted = 2,
}

/// <summary>One timestamped byte sequence in a BlueTusk protocol capture.</summary>
public sealed record BlueTuskProtocolCaptureRecord(
    BlueTuskCaptureDirection Direction,
    BlueTuskCaptureRecordAttributes Attributes,
    TimeSpan Elapsed,
    ReadOnlyMemory<byte> Payload);

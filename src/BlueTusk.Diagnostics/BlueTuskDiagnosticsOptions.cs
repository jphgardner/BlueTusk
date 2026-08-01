namespace BlueTusk.Diagnostics;

/// <summary>Controls opt-in BlueTusk diagnostics behavior for a data source.</summary>
public sealed record BlueTuskDiagnosticsOptions
{
    /// <summary>
    /// Gets the elapsed-time threshold for redacted slow-command events, or null to disable them.
    /// </summary>
    public TimeSpan? SlowCommandThreshold { get; init; }

    internal void Validate()
    {
        if (SlowCommandThreshold is { } threshold && threshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SlowCommandThreshold),
                threshold,
                "The slow-command threshold cannot be negative.");
        }
    }
}

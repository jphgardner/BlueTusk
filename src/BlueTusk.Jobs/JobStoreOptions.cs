namespace BlueTusk.Jobs;

public sealed record JobStoreOptions
{
    public string Schema { get; init; } = "bluetusk_jobs";
    public int MaximumPayloadBytes { get; init; } = 1_048_576;
    public int MaximumClaimBatch { get; init; } = 128;
    public int MaximumClaimPayloadBytes { get; init; } = 8_388_608;
    public int MaximumHistoryEntries { get; init; } = 32;
    public int CommandTimeoutSeconds { get; init; } = 30;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Schema);
        if (Schema.Length > 63 || !(char.IsAsciiLetter(Schema[0]) || Schema[0] == '_') ||
            Schema.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("Schema must be an ASCII PostgreSQL identifier of at most 63 characters.", nameof(Schema));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumPayloadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumPayloadBytes, 67_108_864);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumClaimBatch, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumClaimBatch, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumClaimPayloadBytes, MaximumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumClaimPayloadBytes, 1_073_741_824);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumHistoryEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumHistoryEntries, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(CommandTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CommandTimeoutSeconds, 300);
    }
}

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
        BlueTuskGuard.NotNullOrWhiteSpace(Schema);
        if (Schema.Length > 63 || !(BlueTuskRuntime.IsAsciiLetter(Schema[0]) || Schema[0] == '_') ||
            Schema.Any(character => !BlueTuskRuntime.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("Schema must be an ASCII PostgreSQL identifier of at most 63 characters.", nameof(Schema));
        }

        BlueTuskGuard.NotLessThan(MaximumPayloadBytes, 1);
        BlueTuskGuard.NotGreaterThan(MaximumPayloadBytes, 67_108_864);
        BlueTuskGuard.NotLessThan(MaximumClaimBatch, 1);
        BlueTuskGuard.NotGreaterThan(MaximumClaimBatch, 256);
        BlueTuskGuard.NotLessThan(MaximumClaimPayloadBytes, MaximumPayloadBytes);
        BlueTuskGuard.NotGreaterThan(MaximumClaimPayloadBytes, 1_073_741_824);
        BlueTuskGuard.NotLessThan(MaximumHistoryEntries, 1);
        BlueTuskGuard.NotGreaterThan(MaximumHistoryEntries, 256);
        BlueTuskGuard.NotLessThan(CommandTimeoutSeconds, 1);
        BlueTuskGuard.NotGreaterThan(CommandTimeoutSeconds, 300);
    }
}

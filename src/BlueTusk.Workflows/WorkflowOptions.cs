namespace BlueTusk.Workflows;

public sealed record WorkflowOptions
{
    public string Schema { get; init; } = "bluetusk_workflows";
    public int MaximumNodes { get; init; } = 128;
    public int MaximumDependenciesPerNode { get; init; } = 32;
    public int MaximumDefinitionBytes { get; init; } = 262_144;
    public int MaximumInputBytes { get; init; } = 262_144;
    public int MaximumResultBytes { get; init; } = 65_536;
    public int MaximumActivityInputBytes { get; init; } = 4_194_304;
    public int MaximumSignalBytes { get; init; } = 65_536;
    public int MaximumSignalsPerWorkflow { get; init; } = 128;
    public int MaximumHistoryEntries { get; init; } = 8192;
    public int MaximumBatchSize { get; init; } = 128;
    public int MaximumNodeReadBytes { get; init; } = 4_194_304;
    public int CommandTimeoutSeconds { get; init; } = 30;

    internal void Validate()
    {
        Name(Schema, nameof(Schema), 63);
        if (!(char.IsAsciiLetter(Schema[0]) || Schema[0] == '_') || Schema.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            throw new ArgumentException("Workflow schema must be an ASCII PostgreSQL identifier.", nameof(Schema));
        }

        Range(MaximumNodes, 1, 1024, nameof(MaximumNodes));
        Range(MaximumDependenciesPerNode, 1, 128, nameof(MaximumDependenciesPerNode));
        Range(MaximumDefinitionBytes, 1024, 4_194_304, nameof(MaximumDefinitionBytes));
        Range(MaximumInputBytes, 1, 16_777_216, nameof(MaximumInputBytes));
        Range(MaximumResultBytes, 1, 16_777_216, nameof(MaximumResultBytes));
        Range(MaximumActivityInputBytes, MaximumInputBytes, 67_108_864, nameof(MaximumActivityInputBytes));
        Range(MaximumSignalBytes, 1, MaximumResultBytes, nameof(MaximumSignalBytes));
        Range(MaximumSignalsPerWorkflow, 1, 10000, nameof(MaximumSignalsPerWorkflow));
        Range(MaximumHistoryEntries, MaximumNodes * 4 + 8, 2_000_000, nameof(MaximumHistoryEntries));
        Range(MaximumBatchSize, 1, 256, nameof(MaximumBatchSize));
        Range(MaximumNodeReadBytes, MaximumResultBytes, 67_108_864, nameof(MaximumNodeReadBytes));
        Range(CommandTimeoutSeconds, 1, 300, nameof(CommandTimeoutSeconds));
    }

    internal static void Name(string value, string parameter, int maximum = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > maximum || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Workflow identity exceeds its bound or contains a null character.", parameter);
        }
    }

    internal static void Range(int value, int minimum, int maximum, string parameter)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameter);
        }
    }
}

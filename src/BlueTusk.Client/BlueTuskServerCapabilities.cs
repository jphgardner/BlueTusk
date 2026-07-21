using BlueTusk.Protocol;

namespace BlueTusk.Client;

/// <summary>A connection-scoped snapshot of detected PostgreSQL capabilities.</summary>
public sealed record BlueTuskServerCapabilities
{
    public required Version ServerVersion { get; init; }

    public required BlueTuskProtocolVersion ProtocolVersion { get; init; }

    public bool SupportsPipelineMode { get; init; }

    public bool SupportsMerge { get; init; }

    public bool SupportsMultiranges { get; init; }

    public bool SupportsSqlPgq { get; init; }

    public bool SupportsVirtualGeneratedColumns { get; init; }

    public bool SupportsOAuthBearer { get; init; }

    public IReadOnlyDictionary<string, Version> Extensions { get; init; } =
        new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
}


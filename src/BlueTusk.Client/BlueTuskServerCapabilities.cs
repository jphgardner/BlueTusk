using BlueTusk.Protocol;

namespace BlueTusk.Client;

/// <summary>A connection-scoped snapshot of detected PostgreSQL capabilities.</summary>
public sealed record BlueTuskServerCapabilities
{
    public static BlueTuskServerCapabilities Unknown { get; } = new()
    {
        ServerVersion = new Version(0, 0),
        ProtocolVersion = BlueTuskProtocolVersion.Version30,
    };

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

    internal static BlueTuskServerCapabilities Detect(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var serverVersion = parameters.TryGetValue("server_version", out var value)
            ? ParseServerVersion(value)
            : new Version(0, 0);

        return new BlueTuskServerCapabilities
        {
            ServerVersion = serverVersion,
            ProtocolVersion = BlueTuskProtocolVersion.Version30,
            SupportsPipelineMode = serverVersion.Major >= 14,
            SupportsMerge = serverVersion.Major >= 15,
            SupportsMultiranges = serverVersion.Major >= 14,
            // SQL/PGQ requires an explicit PostgreSQL 19 catalogue/syntax probe before it is enabled.
            SupportsSqlPgq = false,
            SupportsVirtualGeneratedColumns = serverVersion.Major >= 18,
            // Successful OAUTHBEARER negotiation promotes this connection-scoped value to true.
            SupportsOAuthBearer = false,
        };
    }

    private static Version ParseServerVersion(string value)
    {
        var span = value.AsSpan().Trim();
        var majorLength = 0;
        while (majorLength < span.Length && char.IsAsciiDigit(span[majorLength]))
        {
            majorLength++;
        }

        if (majorLength == 0 || !int.TryParse(span[..majorLength], out var major))
        {
            return new Version(0, 0);
        }

        var minor = 0;
        if (majorLength < span.Length && span[majorLength] == '.')
        {
            var minorStart = majorLength + 1;
            var minorLength = 0;
            while (minorStart + minorLength < span.Length && char.IsAsciiDigit(span[minorStart + minorLength]))
            {
                minorLength++;
            }

            if (minorLength > 0)
            {
                _ = int.TryParse(span.Slice(minorStart, minorLength), out minor);
            }
        }

        return new Version(major, minor);
    }
}

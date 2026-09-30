using System.Text;

namespace BlueTusk.Schema;

/// <summary>Admission limits apply before sorting, hashing or materializing JSON object graphs.</summary>
public sealed record SchemaSnapshotLimits
{
    public int MaximumRelations { get; init; } = 10_000;
    public int MaximumColumns { get; init; } = 200_000;
    public int MaximumConstraints { get; init; } = 100_000;
    public int MaximumIndexes { get; init; } = 100_000;
    public int MaximumPolicies { get; init; } = 100_000;
    public int MaximumStringBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumMetadataBytes { get; init; } = 64 * 1024 * 1024;

    internal static SchemaSnapshotLimits Default { get; } = new();

    internal void Validate()
    {
        if (MaximumRelations is < 1 or > 1_000_000 || MaximumColumns is < 1 or > 2_000_000 ||
            MaximumConstraints is < 1 or > 1_000_000 || MaximumIndexes is < 1 or > 1_000_000 ||
            MaximumPolicies is < 1 or > 1_000_000 || MaximumStringBytes is < 1 or > 64 * 1024 * 1024 ||
            MaximumMetadataBytes is < 1 or > 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumMetadataBytes), "Invalid snapshot admission limits.");
        }
    }
}

internal sealed class SchemaModelBudget(SchemaSnapshotLimits limits)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public long UsedBytes { get; private set; }

    public void Add(long bytes)
    {
        UsedBytes += bytes;
        if (UsedBytes > limits.MaximumMetadataBytes) { throw new SchemaCaptureLimitException(); }
    }

    public void Text(string? value, bool required = false, bool identifier = false)
    {
        if (value is null)
        {
            if (required) { throw new ArgumentException("Required schema metadata must not be null."); }
            return;
        }
        if (value.Contains('\0', StringComparison.Ordinal) || required && value.Length == 0)
        {
            throw new ArgumentException("Schema metadata must be nonempty where required and contain no NUL characters.");
        }
        int bytes;
        try { bytes = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException error) { throw new ArgumentException("Schema metadata must be valid Unicode.", error); }
        if (identifier && bytes > 63) { throw new ArgumentException("PostgreSQL identifiers are bounded to 63 UTF-8 bytes."); }
        if (bytes > limits.MaximumStringBytes) { throw new SchemaCaptureLimitException(); }
        Add(bytes);
    }

    public static List<T> Materialize<T>(IEnumerable<T> source, int maximum, Action<T> validate) where T : class
    {
        if (source.TryGetNonEnumeratedCount(out var count) && count > maximum) { throw new SchemaCaptureLimitException(); }
        var values = new List<T>();
        foreach (var item in source)
        {
            if (values.Count == maximum) { throw new SchemaCaptureLimitException(); }
            if (item is null) { throw new ArgumentException("Schema collections must not contain null members.", nameof(source)); }
            validate(item);
            values.Add(item);
        }
        return values;
    }
}

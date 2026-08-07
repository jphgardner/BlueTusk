using System.Text.RegularExpressions;

namespace BlueTusk.Sync.OpenSearch;

/// <summary>Configures an OpenSearch Sync destination.</summary>
public sealed partial record OpenSearchSyncOptions
{
    /// <summary>Gets the reusable client used to reach the OpenSearch cluster.</summary>
    public required HttpClient Client { get; init; }

    /// <summary>Gets the prefix used for BlueTusk-owned indexes and aliases.</summary>
    public string IndexPrefix { get; init; } = "bluetusk-sync";

    /// <summary>Gets the number of primary shards created for materialized indexes.</summary>
    public int NumberOfShards { get; init; } = 1;

    /// <summary>Gets the number of replica shards created for control and materialized indexes.</summary>
    public int NumberOfReplicas { get; init; }

    /// <summary>Gets the active-shard durability requirement used by bulk requests.</summary>
    public string WaitForActiveShards { get; init; } = "all";

    /// <summary>Gets the maximum encoded size of one materialized document.</summary>
    public int MaxDocumentBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Gets the maximum encoded size of one bulk request.</summary>
    public long MaxBulkBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>Gets the maximum source mutations accepted in one transaction.</summary>
    public int MaxMutationsPerTransaction { get; init; } = 10_000;

    /// <summary>Gets the maximum UTF-8 byte length of a reconciled logical key.</summary>
    public int MaxReconciliationKeyBytes { get; init; } = 8 * 1024;

    /// <summary>Gets the maximum sidecar entries returned by one reconciliation search.</summary>
    public int ReconciliationPageSize { get; init; } = 512;

    /// <summary>Gets whether writes wait until their changes become searchable.</summary>
    public bool RefreshAfterWrite { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Client);
        if (Client.BaseAddress is null || !Client.BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "The OpenSearch HttpClient must have an absolute BaseAddress.",
                nameof(Client));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(IndexPrefix);
        if (IndexPrefix.Length > 80 || !IndexPrefixExpression().IsMatch(IndexPrefix))
        {
            throw new ArgumentException(
                "The OpenSearch index prefix must be 1-80 lowercase ASCII letters, digits, hyphens, or underscores and must start with a letter or digit.",
                nameof(IndexPrefix));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(NumberOfShards);
        ArgumentOutOfRangeException.ThrowIfNegative(NumberOfReplicas);
        ArgumentException.ThrowIfNullOrWhiteSpace(WaitForActiveShards);
        if (WaitForActiveShards != "all" &&
            (!int.TryParse(
                WaitForActiveShards,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var activeShards) || activeShards < 1))
        {
            throw new ArgumentException(
                "WaitForActiveShards must be 'all' or a positive integer.",
                nameof(WaitForActiveShards));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBulkBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)MaxDocumentBytes, MaxBulkBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMutationsPerTransaction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxReconciliationKeyBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxReconciliationKeyBytes, 32_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReconciliationPageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ReconciliationPageSize, 10_000);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IndexPrefixExpression();
}

/// <summary>Represents an OpenSearch Sync destination failure.</summary>
public class OpenSearchSyncException : Exception
{
    /// <summary>Initializes a new instance with an error message.</summary>
    public OpenSearchSyncException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with an error message and inner failure.</summary>
    public OpenSearchSyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Indicates that an existing pipeline belongs to another source.</summary>
public sealed class OpenSearchSyncSourceMismatchException : OpenSearchSyncException
{
    /// <summary>Initializes a new instance with an error message.</summary>
    public OpenSearchSyncSourceMismatchException(string message)
        : base(message)
    {
    }
}

/// <summary>Indicates an invalid OpenSearch snapshot lifecycle transition.</summary>
public sealed class OpenSearchSyncSnapshotException : OpenSearchSyncException
{
    /// <summary>Initializes a new instance with an error message.</summary>
    public OpenSearchSyncSnapshotException(string message)
        : base(message)
    {
    }
}

/// <summary>Indicates that an OpenSearch bulk materialization was rejected.</summary>
public sealed class OpenSearchSyncBulkException : OpenSearchSyncException
{
    /// <summary>Initializes a new instance with an error message.</summary>
    public OpenSearchSyncBulkException(string message)
        : base(message)
    {
    }
}

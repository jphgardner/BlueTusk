using System.Text.Json;

namespace BlueTusk.Search.OpenSearch;

public enum SearchHttpClientOwnership { Borrowed, Owned }
public sealed record OpenSearchStoreOptions
{
    public required Uri Endpoint { get; init; }
    public required string IndexName { get; init; }
    public int PrimaryShards { get; init; } = 1;
    public int Replicas { get; init; } = 1;
    public int VectorDimensions { get; init; }
    public SearchStoreOptions Limits { get; init; } = new();
    public int MaxRequestBytes { get; init; } = 32 * 1024 * 1024;
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxConcurrentOperations { get; init; } = 8;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record OpenSearchPage(IReadOnlyList<SearchHit> Hits, int? NextAfterRank, DateTimeOffset ExpiresAt);

/// <summary>Caller-owned bounded rank snapshot. It is process-local and cannot be resumed after host restart.</summary>
public sealed class OpenSearchSearchSession
{
    internal OpenSearchSearchSession(Guid owner, string scope, IReadOnlyList<SearchHit> hits, DateTimeOffset expiresAt, bool databaseDeadline = false)
    {
        Owner = owner;
        Scope = scope;
        Hits = hits;
        ExpiresAt = expiresAt;
        DatabaseDeadline = databaseDeadline;
    }
    internal Guid Owner { get; }
    internal string Scope { get; }
    internal IReadOnlyList<SearchHit> Hits { get; }
    internal bool DatabaseDeadline { get; }
    public DateTimeOffset ExpiresAt { get; }
    public int CandidateCount => Hits.Count;
}

public sealed class OpenSearchResponseException(int statusCode)
    : Exception($"The OpenSearch request failed or returned an invalid/partial response (HTTP {statusCode}).")
{
    public int StatusCode { get; } = statusCode;
}

internal sealed record OpenSearchStoredVersion(long Version, string Fingerprint, long Sequence, long PrimaryTerm);

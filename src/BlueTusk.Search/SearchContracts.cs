using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueTusk.Search;

public enum SearchDataSourceOwnership { Borrowed, Owned }
public enum SearchMode { FullText, Vector, Hybrid }
public enum SearchIngestionStatus { Applied, AlreadyApplied, StaleIgnored }
public sealed record SearchIngestionResult(SearchIngestionStatus Status, long CurrentVersion, int ChunkCount);
public sealed record SearchCursor(Guid QueryId, int AfterRank);
public sealed record SearchHit(string DocumentId, int ChunkOrdinal, long SourceVersion, string Title, string Content, JsonElement Metadata, double Score);
public sealed record SearchPage(IReadOnlyList<SearchHit> Hits, SearchCursor? NextCursor, DateTimeOffset ExpiresAt);
public sealed record SearchRankingScore(string DocumentId, int ChunkOrdinal, double Score);

public interface ISearchRankingExtension
{
    ValueTask<IReadOnlyList<SearchRankingScore>> RankAsync(SearchScope scope, string queryText, IReadOnlyList<SearchHit> candidates, CancellationToken cancellationToken = default);
}

public sealed class SearchScope
{
    public SearchScope(string tenant, string index, IReadOnlyList<string>? principals = null)
    {
        SearchValidation.Key(tenant, nameof(tenant), 256);
        SearchValidation.Key(index, nameof(index), 256);
        Tenant = tenant;
        Index = index;
        Principals = Array.AsReadOnly(SearchValidation.Principals(principals));
        PrincipalsJson = JsonSerializer.Serialize(Principals.ToArray(), SearchJsonContext.Default.StringArray);
        Fingerprint = SearchValidation.Hash(tenant + "\0" + index + "\0" + PrincipalsJson);
    }

    public string Tenant { get; }
    public string Index { get; }
    public IReadOnlyList<string> Principals { get; }
    internal string PrincipalsJson { get; }
    internal string Fingerprint { get; }
}

public sealed class SearchDocument
{
    public SearchDocument(string tenant, string index, string id, long version, string title, string content, bool isPublic = false, IReadOnlyList<string>? principals = null, JsonElement? metadata = null)
    {
        Scope = new SearchScope(tenant, index, principals);
        SearchValidation.Key(id, nameof(id), 512);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(content);
        if (title.Contains('\0', StringComparison.Ordinal) || content.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Indexed text cannot contain NUL.");
        }

        if (metadata is not null && metadata.Value.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("Search metadata must be a JSON object.", nameof(metadata));
        }

        Id = id;
        Version = version;
        Title = title;
        Content = content;
        IsPublic = isPublic;
        MetadataJson = metadata?.GetRawText() ?? "{}";
    }

    public SearchScope Scope { get; }
    public string Id { get; }
    public long Version { get; }
    public string Title { get; }
    public string Content { get; }
    public bool IsPublic { get; }
    public JsonElement Metadata => JsonSerializer.Deserialize(MetadataJson, SearchJsonContext.Default.JsonElement);
    internal string MetadataJson { get; }
}

public sealed record SearchRequest
{
    public SearchMode Mode { get; init; } = SearchMode.FullText;
    public string Text { get; init; } = string.Empty;
    public ReadOnlyMemory<float> Vector { get; init; }
    public int PageSize { get; init; } = 20;
    public int CandidateLimit { get; init; } = 200;
    public double FullTextWeight { get; init; } = 1;
    public double VectorWeight { get; init; } = 1;
    public int ReciprocalRankConstant { get; init; } = 60;
    public bool ApproximateVectorSearch { get; init; }
}

public sealed record SearchStoreOptions
{
    public string Schema { get; init; } = "bluetusk_search";
    public int MaxDocumentBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxTitleBytes { get; init; } = 4096;
    public int MaxMetadataBytes { get; init; } = 64 * 1024;
    public int MaxChunksPerDocument { get; init; } = 2048;
    public int MaxChunkCharacters { get; init; } = 2048;
    public int ChunkOverlapCharacters { get; init; } = 128;
    public int EmbeddingBatchSize { get; init; } = 32;
    public int MaxConcurrentIngestions { get; init; } = 8;
    public int MaxConcurrentRankings { get; init; } = 8;
    public int MaxCandidateCount { get; init; } = 1000;
    public int MaxPageSize { get; init; } = 100;
    public int MaxActiveQueriesPerScope { get; init; } = 128;
    public int MaxRetainedQueries { get; init; } = 4096;
    public long MaxRetainedRankRows { get; init; } = 1_000_000;
    public long MaxRetainedRankBytes { get; init; } = 512L * 1024 * 1024;
    public long MaxRerankingBytes { get; init; } = 8L * 1024 * 1024;
    public long MaxPageBytes { get; init; } = 8L * 1024 * 1024;
    public TimeSpan QueryLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public int CommandTimeoutSeconds { get; init; } = 30;

    internal void Validate()
    {
        _ = SearchValidation.Identifier(Schema);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTitleBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMetadataBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxChunksPerDocument);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxChunkCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(ChunkOverlapCharacters);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ChunkOverlapCharacters, MaxChunkCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(EmbeddingBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrentIngestions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrentRankings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCandidateCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxActiveQueriesPerScope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRetainedQueries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRetainedRankRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRetainedRankBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRerankingBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CommandTimeoutSeconds);
        if (MaxChunksPerDocument > 4096 || MaxChunkCharacters is < 2 or > 16_384 || EmbeddingBatchSize > 256 ||
            MaxConcurrentIngestions > 256 || MaxConcurrentRankings > 256 || MaxCandidateCount > 10_000 || MaxPageSize > MaxCandidateCount ||
            MaxActiveQueriesPerScope > 10_000 || MaxRetainedQueries > 1_000_000 ||
            MaxRetainedRankRows > 100_000_000 || MaxRetainedRankBytes > 1L * 1024 * 1024 * 1024 * 1024 ||
            QueryLifetime <= TimeSpan.Zero || QueryLifetime > TimeSpan.FromHours(1))
        {
            throw new ArgumentException("Search options exceed bounded ingestion, retrieval or retention limits.");
        }

        if (MaxDocumentBytes > 64 * 1024 * 1024 || MaxMetadataBytes > 1024 * 1024 || MaxTitleBytes > 16_384 ||
            MaxMetadataBytes > MaxDocumentBytes || MaxTitleBytes > MaxDocumentBytes ||
            MaxPageBytes > 256L * 1024 * 1024 || MaxRerankingBytes > 256L * 1024 * 1024)
        {
            throw new ArgumentException("Search options exceed supported document, metadata, title or result byte budgets.");
        }
    }
}

/// <summary>Application-supplied embedding service; model identity is part of the persistent index contract.</summary>
public interface ISearchEmbeddingProvider
{
    string ModelIdentity { get; }
    ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

/// <summary>Optional tenant/index context for scoped durable embedding checkpoints. Hosts derive scopes from trusted authorization state.</summary>
public interface IScopedSearchEmbeddingProvider : ISearchEmbeddingProvider
{
    ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedForScopeAsync(SearchScope scope, IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

/// <summary>Trusted PostgreSQL extension adapter. SQL fragments receive library-generated column/parameter references, never user input.</summary>
public interface IPostgreSqlSearchVectorAdapter
{
    string StorageContract { get; }
    int Dimensions { get; }
    string ColumnTypeSql { get; }
    string ParameterSql(string trustedParameterSql);
    string DistanceSql(string trustedColumnSql, string trustedParameterSql);
    string Encode(ReadOnlySpan<float> vector);
    ValueTask ValidateInstallationAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken = default);
}

public sealed class SearchVersionConflictException : Exception
{
    public SearchVersionConflictException(string id, long version)
        : base($"Search document '{id}' has incompatible payloads at source version {version}.")
    {
        DocumentId = id;
        Version = version;
    }

    public string DocumentId { get; }
    public long Version { get; }
}

public sealed class SearchCursorExpiredException : Exception
{
    public SearchCursorExpiredException() : base("The search cursor is expired, missing, or belongs to a different tenant, index or permission scope.") { }
}

public sealed class SearchBackpressureException : Exception
{
    public SearchBackpressureException() : base("The bounded search ingestion or embedding capacity is full; retry through an upstream bounded queue.") { }
}

internal static class SearchValidation
{
    internal static void Key(string value, string name, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(value) > maxBytes)
        {
            throw new ArgumentException($"Value must fit within {maxBytes} UTF-8 bytes and cannot contain NUL.", name);
        }
    }

    internal static string Identifier(string value)
    {
        Key(value, nameof(value), 63);
        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    internal static string[] Principals(IReadOnlyList<string>? input)
    {
        if (input is null)
        {
            return [];
        }

        if (input.Count > 128)
        {
            throw new ArgumentException("At most 128 principals can be supplied.", nameof(input));
        }

        foreach (var principal in input)
        {
            Key(principal, nameof(input), 256);
        }

        return input.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static string Hash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class SearchJsonContext : JsonSerializerContext;

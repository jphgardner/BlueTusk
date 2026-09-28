using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlueTusk.Search.OpenSearch;

public sealed partial class OpenSearchStore
{
    /// <summary>Ranks a bounded candidate snapshot using one PIT, then releases that server resource. The caller retains the bounded session.</summary>
    public async ValueTask<OpenSearchSearchSession> SearchAsync(SearchScope scope, SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Mode) || request.CandidateLimit <= 0 || request.CandidateLimit > Options.Limits.MaxCandidateCount ||
            request.PageSize <= 0 || request.PageSize > Options.Limits.MaxPageSize || request.Text is null || Encoding.UTF8.GetByteCount(request.Text) > 16_384 ||
            request.Text.Contains('\0', StringComparison.Ordinal) || !double.IsFinite(request.FullTextWeight) || !double.IsFinite(request.VectorWeight) ||
            request.FullTextWeight <= 0 || request.VectorWeight <= 0 || request.ReciprocalRankConstant is < 1 or > 10_000)
        { throw new ArgumentException("Search request exceeds the supported query/ranking bounds.", nameof(request)); }
        if (request.Mode is not SearchMode.FullText && Options.VectorDimensions == 0) { throw new InvalidOperationException("This index does not support vector retrieval."); }
        if (request.Mode is not SearchMode.Vector && string.IsNullOrWhiteSpace(request.Text)) { throw new ArgumentException("A full-text query requires text.", nameof(request)); }
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        string? pit = null;
        try
        {
            ReadOnlyMemory<float> vector = request.Vector.ToArray();
            if (request.Mode is not SearchMode.FullText)
            {
                if (vector.IsEmpty)
                {
                    if (string.IsNullOrWhiteSpace(request.Text)) { throw new ArgumentException("Vector retrieval requires a vector or query text.", nameof(request)); }
                    vector = (await EmbedAsync(scope, [request.Text], cancellationToken).ConfigureAwait(false))[0];
                }
                ValidateVector(vector.Span);
            }
            using (var created = await RequiredAsync(HttpMethod.Post, Options.IndexName + "/_search/point_in_time?keep_alive=1m", null, cancellationToken).ConfigureAwait(false))
            { pit = created.RootElement.GetProperty("pit_id").GetString() ?? throw new OpenSearchResponseException(200); }
            var text = request.Mode is SearchMode.Vector ? Array.Empty<SearchHit>() :
                await CandidatesAsync(scope, pit, request.Text, default, request.CandidateLimit, false, cancellationToken).ConfigureAwait(false);
            var vectors = request.Mode is SearchMode.FullText ? Array.Empty<SearchHit>() :
                await CandidatesAsync(scope, pit, string.Empty, vector, request.CandidateLimit, request.ApproximateVectorSearch, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SearchHit> ranked;
            if (request.Mode is SearchMode.Hybrid)
            {
                var scores = new Dictionary<string, (SearchHit Hit, double Score)>(StringComparer.Ordinal);
                AddRanks(text, request.FullTextWeight, request.ReciprocalRankConstant, scores);
                AddRanks(vectors, request.VectorWeight, request.ReciprocalRankConstant, scores);
                ranked = scores.Values.Select(static item => item.Hit with { Score = item.Score })
                    .OrderByDescending(static hit => hit.Score).ThenBy(static hit => hit.DocumentId, StringComparer.Ordinal).ThenBy(static hit => hit.ChunkOrdinal)
                    .Take(request.CandidateLimit).ToArray();
            }
            else { ranked = request.Mode is SearchMode.FullText ? text : vectors; }
            long bytes = 0;
            foreach (var hit in ranked)
            {
                bytes += HitBytes(hit);
                if (bytes > Options.Limits.MaxRerankingBytes) { throw new ArgumentException("Rank snapshot exceeds its bounded payload byte budget.", nameof(request)); }
            }
            return new(_owner, ScopeKey(scope), ranked, DateTimeOffset.UtcNow + Options.Limits.QueryLifetime);
        }
        finally
        {
            try { if (pit is not null) { await ClosePitAsync(pit).ConfigureAwait(false); } }
            finally { _operations.Release(); }
        }
    }

    /// <summary>Every page rechecks current real-time versions, deletion and ACLs. A revoked/replaced document is skipped within the stable ranks.</summary>
    public async ValueTask<OpenSearchPage> ReadPageAsync(SearchScope scope, OpenSearchSearchSession session, int pageSize = 20, int afterRank = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(session);
        if (session.Owner != _owner || session.Scope != ScopeKey(scope) || !session.DatabaseDeadline && session.ExpiresAt <= DateTimeOffset.UtcNow) { throw new SearchCursorExpiredException(); }
        if (pageSize <= 0 || pageSize > Options.Limits.MaxPageSize || afterRank < 0 || afterRank > session.CandidateCount) { throw new ArgumentOutOfRangeException(nameof(pageSize)); }
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hits = new List<SearchHit>(pageSize);
            long bytes = 0;
            var rank = afterRank;
            while (rank < session.CandidateCount && hits.Count < pageSize)
            {
                var count = Math.Min(Math.Min(pageSize - hits.Count, 100), session.CandidateCount - rank);
                var candidates = session.Hits.Skip(rank).Take(count).ToArray();
                var body = new JsonObject { ["ids"] = Strings(candidates.Select(hit => DocumentKey(scope, hit.DocumentId))) };
                using var response = await RequiredAsync(HttpMethod.Post, Options.IndexName + "/_mget?_source_includes=tenant,index_name,document_id,source_version,deleted,public,principals", body, cancellationToken).ConfigureAwait(false);
                var current = response.RootElement.GetProperty("docs");
                if (current.GetArrayLength() != candidates.Length) { throw new OpenSearchResponseException(200); }
                for (var i = 0; i < candidates.Length; i++)
                {
                    var item = current[i];
                    var candidate = candidates[i];
                    if (item.TryGetProperty("found", out var found) && found.GetBoolean())
                    {
                        var source = item.GetProperty("_source");
                        if (source.GetProperty("document_id").GetString() == candidate.DocumentId && Authorized(source, scope) && source.GetProperty("source_version").GetInt64() == candidate.SourceVersion)
                        {
                            var payload = HitBytes(candidate);
                            if (bytes + payload > Options.Limits.MaxPageBytes)
                            {
                                if (hits.Count == 0) { throw new ArgumentException("A ranked hit exceeds the page byte budget."); }
                                return new(hits.AsReadOnly(), rank, session.ExpiresAt);
                            }
                            hits.Add(candidate);
                            bytes += payload;
                        }
                    }
                    else if (item.TryGetProperty("error", out _)) { throw new OpenSearchResponseException(200); }
                    rank++;
                }
            }
            return new(hits.AsReadOnly(), rank < session.CandidateCount ? rank : null, session.ExpiresAt);
        }
        finally { _operations.Release(); }
    }

    private async ValueTask<IReadOnlyList<SearchHit>> CandidatesAsync(SearchScope scope, string pit, string text, ReadOnlyMemory<float> vector,
        int maximum, bool approximate, CancellationToken cancellationToken)
    {
        JsonObject clause;
        if (vector.IsEmpty)
        {
            clause = new JsonObject { ["match"] = new JsonObject { ["chunks.text"] = new JsonObject { ["query"] = text } } };
        }
        else if (approximate)
        {
            // Lucene nested HNSW accepts top-level field filters inside kNN; authorization participates in candidate admission.
            clause = new JsonObject { ["knn"] = new JsonObject { ["chunks.embedding"] = new JsonObject
            {
                ["vector"] = VectorJson(vector.Span), ["k"] = maximum,
                ["filter"] = new JsonObject { ["bool"] = new JsonObject { ["filter"] = AuthorizationFilter(scope) } },
            } } };
        }
        else
        {
            clause = new JsonObject
            {
                ["script_score"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["match_all"] = new JsonObject() },
                    ["script"] = new JsonObject { ["lang"] = "knn", ["source"] = "knn_score", ["params"] = new JsonObject
                        { ["field"] = "chunks.embedding", ["query_value"] = VectorJson(vector.Span), ["space_type"] = "cosinesimil" } },
                },
            };
        }
        var nested = new JsonObject
        {
            ["nested"] = new JsonObject
            {
                ["path"] = "chunks", ["score_mode"] = "max", ["query"] = clause,
                ["inner_hits"] = new JsonObject { ["size"] = 1, ["_source"] = Strings(["chunks.ordinal", "chunks.content"]) },
            },
        };
        var query = new JsonObject
        {
            ["size"] = maximum, ["track_total_hits"] = false,
            ["pit"] = new JsonObject { ["id"] = pit, ["keep_alive"] = "1m" },
            ["_source"] = Strings(["tenant", "index_name", "document_id", "source_version", "deleted", "public", "principals", "title", "metadata"]),
            ["query"] = new JsonObject { ["bool"] = new JsonObject { ["filter"] = AuthorizationFilter(scope), ["must"] = new JsonArray(nested) } },
            ["sort"] = new JsonArray(new JsonObject { ["_score"] = "desc" }, new JsonObject { ["document_id"] = "asc" }),
        };
        using var response = await RequiredAsync(HttpMethod.Post, "_search?allow_partial_search_results=false", query, cancellationToken).ConfigureAwait(false);
        var root = response.RootElement;
        if (root.GetProperty("timed_out").GetBoolean() || root.GetProperty("_shards").GetProperty("failed").GetInt32() != 0) { throw new OpenSearchResponseException(200); }
        var hits = root.GetProperty("hits").GetProperty("hits");
        if (hits.GetArrayLength() > maximum) { throw new OpenSearchResponseException(200); }
        var result = new List<SearchHit>(hits.GetArrayLength());
        foreach (var entry in hits.EnumerateArray())
        {
            var source = entry.GetProperty("_source");
            if (!Authorized(source, scope)) { throw new OpenSearchResponseException(200); }
            var nestedHit = entry.GetProperty("inner_hits").GetProperty("chunks").GetProperty("hits").GetProperty("hits")[0].GetProperty("_source");
            var score = entry.GetProperty("_score").GetDouble();
            if (!double.IsFinite(score)) { throw new OpenSearchResponseException(200); }
            result.Add(new(source.GetProperty("document_id").GetString()!, nestedHit.GetProperty("ordinal").GetInt32(), source.GetProperty("source_version").GetInt64(),
                source.GetProperty("title").GetString()!, nestedHit.GetProperty("content").GetString()!, source.GetProperty("metadata").Clone(), score));
        }
        return result.AsReadOnly();
    }

    private static JsonArray AuthorizationFilter(SearchScope scope)
    {
        var permissions = new JsonArray(new JsonObject { ["term"] = new JsonObject { ["public"] = true } });
        if (scope.Principals.Count != 0) { permissions.Add(new JsonObject { ["terms"] = new JsonObject { ["principals"] = Strings(scope.Principals) } }); }
        return new JsonArray(
            new JsonObject { ["term"] = new JsonObject { ["tenant"] = scope.Tenant } },
            new JsonObject { ["term"] = new JsonObject { ["index_name"] = scope.Index } },
            new JsonObject { ["term"] = new JsonObject { ["deleted"] = false } },
            new JsonObject { ["bool"] = new JsonObject { ["should"] = permissions, ["minimum_should_match"] = 1 } });
    }

    private static bool Authorized(JsonElement source, SearchScope scope) =>
        source.GetProperty("tenant").GetString() == scope.Tenant && source.GetProperty("index_name").GetString() == scope.Index && !source.GetProperty("deleted").GetBoolean() &&
        (source.GetProperty("public").GetBoolean() || source.GetProperty("principals").EnumerateArray().Any(principal => scope.Principals.Contains(principal.GetString()!, StringComparer.Ordinal)));

    private static void AddRanks(IReadOnlyList<SearchHit> hits, double weight, int constant, Dictionary<string, (SearchHit Hit, double Score)> result)
    {
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var score = weight / (constant + i + 1);
            result[hit.DocumentId] = result.TryGetValue(hit.DocumentId, out var previous) ? (previous.Hit, previous.Score + score) : (hit, score);
        }
    }

    private async ValueTask ClosePitAsync(string pit)
    {
        using var closed = await RequiredAsync(HttpMethod.Delete, "_search/point_in_time", new JsonObject { ["pit_id"] = Strings([pit]) }, CancellationToken.None).ConfigureAwait(false);
    }
    private static long HitBytes(SearchHit hit) => (long)Encoding.UTF8.GetByteCount(hit.DocumentId) + Encoding.UTF8.GetByteCount(hit.Title) + Encoding.UTF8.GetByteCount(hit.Content) + Encoding.UTF8.GetByteCount(hit.Metadata.GetRawText());
}

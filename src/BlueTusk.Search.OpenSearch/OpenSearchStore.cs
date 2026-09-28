using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlueTusk.Search.OpenSearch;

/// <summary>One parent document holds all nested chunks; source versions and tombstones are updated with OpenSearch sequence/term CAS.</summary>
public sealed partial class OpenSearchStore : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly SearchHttpClientOwnership _ownership;
    private readonly ISearchEmbeddingProvider? _embeddings;
    private readonly SemaphoreSlim _operations;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly string _contract;
    private string _indexGeneration = string.Empty;
    private int _disposed;
    private int _initialized;

    public OpenSearchStore(HttpClient httpClient, OpenSearchStoreOptions options, ISearchEmbeddingProvider? embeddings = null,
        SearchHttpClientOwnership ownership = SearchHttpClientOwnership.Borrowed)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        ArgumentNullException.ThrowIfNull(options.Limits);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("https" or "http") ||
            options.Endpoint.UserInfo.Length != 0 || options.Endpoint.Query.Length != 0 || options.Endpoint.Fragment.Length != 0)
        {
            throw new ArgumentException("A trusted absolute HTTP(S) endpoint without embedded credentials/query/fragment is required.", nameof(options));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.IndexName);
        if (options.IndexName.Length > 200 || options.IndexName.Any(static character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character is not '_' and not '-') ||
            options.IndexName[0] is '_' or '-') { throw new ArgumentException("Index name must be a bounded lowercase ASCII OpenSearch identifier.", nameof(options)); }
        if (!Enum.IsDefined(ownership)) { throw new ArgumentOutOfRangeException(nameof(ownership)); }
        ArgumentOutOfRangeException.ThrowIfNegative(options.VectorDimensions);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.VectorDimensions, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PrimaryShards, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.PrimaryShards, 128);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Replicas);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Replicas, 10);
        if ((options.VectorDimensions > 0) != (embeddings is not null)) { throw new ArgumentException("Vector dimensions and an embedding provider must be configured together.", nameof(embeddings)); }
        if (embeddings is not null) { ArgumentException.ThrowIfNullOrWhiteSpace(embeddings.ModelIdentity); }
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentOperations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxConcurrentOperations, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxRequestBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxRequestBytes, 256 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxResponseBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxResponseBytes, 256 * 1024 * 1024);
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(5)) { throw new ArgumentOutOfRangeException(nameof(options)); }
        ValidateLimits(options.Limits);
        Options = options;
        _http = httpClient;
        _embeddings = embeddings;
        _ownership = ownership;
        _operations = new(options.MaxConcurrentOperations, options.MaxConcurrentOperations);
        _contract = Hash(string.Create(CultureInfo.InvariantCulture,
            $"v1:{options.VectorDimensions}:{embeddings?.ModelIdentity}:{options.Limits.MaxChunkCharacters}:{options.Limits.ChunkOverlapCharacters}:{options.Limits.MaxChunksPerDocument}:lucene-hnsw-cosine:standard"));
    }

    public OpenSearchStoreOptions Options { get; }

    /// <summary>Deployment-time index creation. Existing index contracts and relevant mapping fields are validated.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken, requireInitialized: false).ConfigureAwait(false);
        try
        {
            var properties = new JsonObject
            {
                ["tenant"] = Field("keyword"), ["index_name"] = Field("keyword"), ["document_id"] = Field("keyword"),
                ["source_version"] = Field("long"), ["fingerprint"] = Field("keyword"), ["deleted"] = Field("boolean"),
                ["public"] = Field("boolean"), ["principals"] = Field("keyword"), ["title"] = Field("text"),
                ["metadata"] = new JsonObject { ["type"] = "object", ["enabled"] = false },
            };
            var chunkProperties = new JsonObject { ["ordinal"] = Field("integer"), ["content"] = new JsonObject { ["type"] = "text", ["index"] = false }, ["text"] = Field("text") };
            if (Options.VectorDimensions > 0)
            {
                chunkProperties["embedding"] = new JsonObject
                {
                    ["type"] = "knn_vector", ["dimension"] = Options.VectorDimensions,
                    ["method"] = new JsonObject { ["name"] = "hnsw", ["engine"] = "lucene", ["space_type"] = "cosinesimil", ["parameters"] = new JsonObject { ["ef_construction"] = 128, ["m"] = 16 } },
                };
            }

            properties["chunks"] = new JsonObject { ["type"] = "nested", ["properties"] = chunkProperties };
            var mapping = new JsonObject { ["dynamic"] = "strict", ["_meta"] = new JsonObject { ["bluetusk_contract"] = _contract }, ["properties"] = properties };
            var settings = new JsonObject { ["number_of_shards"] = Options.PrimaryShards, ["number_of_replicas"] = Options.Replicas };
            if (Options.VectorDimensions > 0) { settings["index.knn"] = true; }
            var created = await RequestAsync(HttpMethod.Put, Options.IndexName, new JsonObject { ["settings"] = settings, ["mappings"] = mapping }, cancellationToken).ConfigureAwait(false);
            using (created.Body)
            {
                if (created.Status is not 200 && !(created.Status == 400 && created.Body.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("type", out var type) && type.GetString() == "resource_already_exists_exception"))
                { throw new OpenSearchResponseException(created.Status); }
            }

            var response = await RequiredAsync(HttpMethod.Get, Options.IndexName + "/_mapping", null, cancellationToken).ConfigureAwait(false);
            using (response)
            {
                var installed = response.RootElement.GetProperty(Options.IndexName).GetProperty("mappings");
                if (installed.GetProperty("_meta").GetProperty("bluetusk_contract").GetString() != _contract || installed.GetProperty("dynamic").GetString() != "strict")
                { throw new InvalidOperationException("OpenSearch index contract differs from the configured chunk/model/storage definition."); }
                ValidateMapping(installed.GetProperty("properties"), properties);
            }
            _indexGeneration = await ReadIndexGenerationAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _initialized, 1);
        }
        finally { _operations.Release(); }
    }

    public async ValueTask<SearchIngestionResult> UpsertAsync(SearchDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = document.Metadata;
            var metadataJson = metadata.GetRawText();
            var titleBytes = Encoding.UTF8.GetByteCount(document.Title);
            var metadataBytes = Encoding.UTF8.GetByteCount(metadataJson);
            if (titleBytes > Options.Limits.MaxTitleBytes || metadataBytes > Options.Limits.MaxMetadataBytes ||
                (long)titleBytes + metadataBytes + Encoding.UTF8.GetByteCount(document.Content) > Options.Limits.MaxDocumentBytes)
            { throw new ArgumentException("Document exceeds the configured Search text/title/metadata byte limits.", nameof(document)); }
            var chunks = SearchChunker.Chunk(document.Content, Options.Limits.MaxChunkCharacters, Options.Limits.ChunkOverlapCharacters, Options.Limits.MaxChunksPerDocument);
            var fingerprint = Hash(string.Join('\0', _contract, document.Title, document.Content, document.IsPublic.ToString(), PrincipalJson(document.Scope), metadataJson));
            var existing = await ReadVersionAsync(document.Scope, document.Id, cancellationToken).ConfigureAwait(false);
            var replay = Compare(existing, document.Id, document.Version, fingerprint, chunks.Count);
            if (replay is not null) { return replay; }
            var vectors = await EmbedAsync(document.Scope, chunks.Select(static chunk => chunk.Content).ToArray(), cancellationToken).ConfigureAwait(false);
            var source = DocumentSource(document.Scope, document.Id, document.Version, fingerprint, deleted: false, document.Title, metadata, document.IsPublic, document.Scope.Principals);
            var items = new JsonArray();
            foreach (var chunk in chunks)
            {
                var item = new JsonObject { ["ordinal"] = chunk.Ordinal, ["content"] = chunk.Content, ["text"] = document.Title + "\n" + chunk.Content };
                if (Options.VectorDimensions > 0) { item["embedding"] = VectorJson(vectors[chunk.Ordinal].Span); }
                items.Add(item);
            }
            source["chunks"] = items;
            return await WriteVersionAsync(document.Scope, document.Id, document.Version, fingerprint, source, existing, chunks.Count, cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    public async ValueTask<SearchIngestionResult> DeleteAsync(string tenant, string index, string id, long version, CancellationToken cancellationToken = default)
    {
        var document = new SearchDocument(tenant, index, id, version, string.Empty, string.Empty);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fingerprint = Hash("deleted:" + _contract);
            var existing = await ReadVersionAsync(document.Scope, id, cancellationToken).ConfigureAwait(false);
            var source = DocumentSource(document.Scope, id, version, fingerprint, deleted: true, string.Empty, document.Metadata, false, []);
            source["chunks"] = new JsonArray();
            return await WriteVersionAsync(document.Scope, id, version, fingerprint, source, existing, 0, cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        for (var i = 0; i < Options.MaxConcurrentOperations; i++) { await _operations.WaitAsync().ConfigureAwait(false); }
        _operations.Dispose();
        if (_ownership is SearchHttpClientOwnership.Owned) { _http.Dispose(); }
    }

    private async ValueTask<SearchIngestionResult> WriteVersionAsync(SearchScope scope, string id, long version, string fingerprint, JsonObject source,
        OpenSearchStoredVersion? existing, int chunkCount, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var replay = Compare(existing, id, version, fingerprint, chunkCount);
            if (replay is not null) { return replay; }
            var condition = existing is null ? "op_type=create" : string.Create(CultureInfo.InvariantCulture, $"if_seq_no={existing.Sequence}&if_primary_term={existing.PrimaryTerm}");
            var response = await RequestAsync(HttpMethod.Put, Options.IndexName + "/_doc/" + DocumentKey(scope, id) + "?refresh=wait_for&" + condition, source, cancellationToken).ConfigureAwait(false);
            using (response.Body)
            {
                if (response.Status is 200 or 201) { return new(SearchIngestionStatus.Applied, version, chunkCount); }
                if (response.Status != 409) { throw new OpenSearchResponseException(response.Status); }
            }
            existing = await ReadVersionAsync(scope, id, cancellationToken).ConfigureAwait(false);
        }
        throw new SearchBackpressureException();
    }

    private async ValueTask<OpenSearchStoredVersion?> ReadVersionAsync(SearchScope scope, string id, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(HttpMethod.Get, Options.IndexName + "/_doc/" + DocumentKey(scope, id) + "?_source_includes=tenant,index_name,document_id,source_version,fingerprint", null, cancellationToken).ConfigureAwait(false);
        using (response.Body)
        {
            if (response.Status == 404 && response.Body.RootElement.TryGetProperty("found", out var missing) && !missing.GetBoolean()) { return null; }
            if (response.Status != 200) { throw new OpenSearchResponseException(response.Status); }
            var root = response.Body.RootElement;
            var source = root.GetProperty("_source");
            if (source.GetProperty("tenant").GetString() != scope.Tenant || source.GetProperty("index_name").GetString() != scope.Index || source.GetProperty("document_id").GetString() != id)
            { throw new OpenSearchResponseException(200); }
            return new(source.GetProperty("source_version").GetInt64(), source.GetProperty("fingerprint").GetString()!, root.GetProperty("_seq_no").GetInt64(), root.GetProperty("_primary_term").GetInt64());
        }
    }

    private static SearchIngestionResult? Compare(OpenSearchStoredVersion? current, string id, long version, string fingerprint, int chunks)
    {
        if (current is null || current.Version < version) { return null; }
        if (current.Version > version) { return new(SearchIngestionStatus.StaleIgnored, current.Version, 0); }
        if (current.Fingerprint != fingerprint) { throw new SearchVersionConflictException(id, version); }
        return new(SearchIngestionStatus.AlreadyApplied, version, chunks);
    }

    private async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(SearchScope scope, string[] texts, CancellationToken cancellationToken)
    {
        if (_embeddings is null) { return Array.Empty<ReadOnlyMemory<float>>(); }
        var result = new List<ReadOnlyMemory<float>>(texts.Length);
        foreach (var batch in texts.Chunk(Options.Limits.EmbeddingBatchSize))
        {
            var vectors = _embeddings is IScopedSearchEmbeddingProvider scoped
                ? await scoped.EmbedForScopeAsync(scope, batch, cancellationToken).ConfigureAwait(false)
                : await _embeddings.EmbedAsync(batch, cancellationToken).ConfigureAwait(false);
            if (vectors.Count != batch.Length) { throw new ArgumentException("Embedding provider returned an incompatible batch."); }
            foreach (var vector in vectors) { ValidateVector(vector.Span); result.Add(vector.ToArray()); }
        }
        return result.AsReadOnly();
    }

    private void ValidateVector(ReadOnlySpan<float> vector)
    {
        if (vector.Length != Options.VectorDimensions) { throw new ArgumentException("Vector dimensions differ from the index contract.", nameof(vector)); }
        var nonzero = false;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value)) { throw new ArgumentException("Cosine vectors must be finite.", nameof(vector)); }
            nonzero |= value != 0;
        }
        if (!nonzero) { throw new ArgumentException("Cosine vectors must have nonzero norm.", nameof(vector)); }
    }

    private async ValueTask EnterAsync(CancellationToken cancellationToken, bool requireInitialized = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!await _operations.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { throw new SearchBackpressureException(); }
        if (Volatile.Read(ref _disposed) != 0) { _operations.Release(); throw new ObjectDisposedException(nameof(OpenSearchStore)); }
        if (requireInitialized && Volatile.Read(ref _initialized) == 0) { _operations.Release(); throw new InvalidOperationException("Initialize and validate the OpenSearch index before using this store."); }
    }

    private async ValueTask<JsonDocument> RequiredAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        var result = await RequestAsync(method, path, body, cancellationToken).ConfigureAwait(false);
        if (result.Status is >= 200 and < 300) { return result.Body; }
        result.Body.Dispose();
        throw new OpenSearchResponseException(result.Status);
    }

    private async ValueTask<(int Status, JsonDocument Body)> RequestAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.RequestTimeout);
        using var request = new HttpRequestMessage(method, new Uri(Options.Endpoint.AbsoluteUri.TrimEnd('/') + "/" + path));
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
            if (bytes.Length > Options.MaxRequestBytes) { throw new ArgumentException("OpenSearch request exceeds its byte budget.", nameof(body)); }
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new("application/json");
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > Options.MaxResponseBytes) { throw new OpenSearchResponseException((int)response.StatusCode); }
        await using var incoming = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[16 * 1024];
        while (true)
        {
            var count = await incoming.ReadAsync(block, timeout.Token).ConfigureAwait(false);
            if (count == 0) { break; }
            if (buffer.Length + count > Options.MaxResponseBytes) { throw new OpenSearchResponseException((int)response.StatusCode); }
            buffer.Write(block, 0, count);
        }
        try { return ((int)response.StatusCode, JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)))); }
        catch (JsonException) { throw new OpenSearchResponseException((int)response.StatusCode); }
    }

    private static JsonObject DocumentSource(SearchScope scope, string id, long version, string fingerprint, bool deleted, string title, JsonElement metadata, bool isPublic, IReadOnlyList<string> principals) => new()
    {
        ["tenant"] = scope.Tenant, ["index_name"] = scope.Index, ["document_id"] = id, ["source_version"] = version,
        ["fingerprint"] = fingerprint, ["deleted"] = deleted, ["public"] = isPublic, ["principals"] = Strings(principals),
        ["title"] = title, ["metadata"] = JsonNode.Parse(metadata.GetRawText()),
    };
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private static JsonArray VectorJson(ReadOnlySpan<float> vector)
    {
        var array = new JsonArray();
        foreach (var value in vector) { array.Add(value); }
        return array;
    }
    private static string PrincipalJson(SearchScope scope) => Strings(scope.Principals).ToJsonString();
    private static string ScopeKey(SearchScope scope) => Hash(string.Join('\0', scope.Tenant, scope.Index, PrincipalJson(scope)));
    private static string DocumentKey(SearchScope scope, string id) => Hash(string.Join('\0', scope.Tenant, scope.Index, id));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static JsonObject Field(string type) => new() { ["type"] = type };

    private static void ValidateMapping(JsonElement installed, JsonObject expected)
    {
        foreach (var entry in expected)
        {
            var actual = installed.GetProperty(entry.Key);
            var definition = entry.Value!.AsObject();
            if (actual.GetProperty("type").GetString() != definition["type"]!.GetValue<string>()) { throw new InvalidOperationException("OpenSearch field mapping differs from the storage contract."); }
            if (definition.TryGetPropertyValue("dimension", out var dimension) && actual.GetProperty("dimension").GetInt32() != dimension!.GetValue<int>()) { throw new InvalidOperationException("OpenSearch vector dimensions differ."); }
            if (definition.TryGetPropertyValue("properties", out var nested)) { ValidateMapping(actual.GetProperty("properties"), nested!.AsObject()); }
            if (definition.TryGetPropertyValue("enabled", out var enabled) && actual.GetProperty("enabled").GetBoolean() != enabled!.GetValue<bool>()) { throw new InvalidOperationException("OpenSearch metadata mapping differs."); }
            if (definition.TryGetPropertyValue("index", out var indexed) && actual.GetProperty("index").GetBoolean() != indexed!.GetValue<bool>()) { throw new InvalidOperationException("OpenSearch payload indexing differs."); }
            if (definition.TryGetPropertyValue("method", out var method))
            {
                var actualMethod = actual.GetProperty("method");
                foreach (var name in new[] { "name", "engine", "space_type" })
                {
                    if (actualMethod.GetProperty(name).GetString() != method![name]!.GetValue<string>()) { throw new InvalidOperationException("OpenSearch vector method differs."); }
                }
            }
        }
    }

    private static void ValidateLimits(SearchStoreOptions limits)
    {
        if (limits.MaxDocumentBytes is < 1 or > 64 * 1024 * 1024 || limits.MaxTitleBytes is < 1 or > 16_384 || limits.MaxMetadataBytes is < 1 or > 1024 * 1024 ||
            limits.MaxChunksPerDocument is < 1 or > 4096 || limits.MaxChunkCharacters is < 2 or > 16_384 || limits.ChunkOverlapCharacters < 0 || limits.ChunkOverlapCharacters >= limits.MaxChunkCharacters ||
            limits.EmbeddingBatchSize is < 1 or > 256 || limits.MaxCandidateCount is < 1 or > 10_000 || limits.MaxPageSize is < 1 or > 10_000 || limits.MaxPageSize > limits.MaxCandidateCount ||
            limits.MaxRerankingBytes is < 1 or > 256L * 1024 * 1024 || limits.MaxPageBytes is < 1 or > 256L * 1024 * 1024 || limits.QueryLifetime <= TimeSpan.Zero || limits.QueryLifetime > TimeSpan.FromHours(1))
        { throw new ArgumentException("OpenSearch limits exceed bounded ingestion/retrieval capacities.", nameof(limits)); }
    }
}

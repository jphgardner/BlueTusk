using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BlueTusk.Search;

/// <summary>Version-fenced PostgreSQL ingestion and scoped retrieval with persisted bounded rank snapshots.</summary>
public sealed partial class PostgreSqlSearchStore : IAsyncDisposable
{
    public const int CurrentStorageVersion = 1;
    private readonly DbDataSource _source;
    private readonly SearchDataSourceOwnership _ownership;
    private readonly IPostgreSqlSearchVectorAdapter? _vectors;
    private readonly ISearchEmbeddingProvider? _embeddings;
    private readonly ISearchRankingExtension? _ranking;
    private readonly SemaphoreSlim _ingestionSlots;
    private readonly string _schema;
    private readonly string _contract;
    private int _disposed;

    public PostgreSqlSearchStore(DbDataSource source, SearchStoreOptions? options = null, IPostgreSqlSearchVectorAdapter? vectors = null, ISearchEmbeddingProvider? embeddings = null, SearchDataSourceOwnership ownership = SearchDataSourceOwnership.Borrowed, ISearchRankingExtension? ranking = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(ownership))
        {
            throw new ArgumentOutOfRangeException(nameof(ownership));
        }

        Options = options ?? new SearchStoreOptions();
        Options.Validate();
        if (vectors is not null && (vectors.Dimensions < 1 || vectors.Dimensions > 16_000 || string.IsNullOrWhiteSpace(vectors.StorageContract)))
        {
            throw new ArgumentException("The vector adapter must declare bounded dimensions and a durable contract.", nameof(vectors));
        }

        if (embeddings is not null && (vectors is null || string.IsNullOrWhiteSpace(embeddings.ModelIdentity)))
        {
            throw new ArgumentException("An embedding provider requires a vector adapter and model identity.", nameof(embeddings));
        }

        _source = source;
        _vectors = vectors;
        _embeddings = embeddings;
        _ranking = ranking;
        _ownership = ownership;
        _schema = SearchValidation.Identifier(Options.Schema);
        _ingestionSlots = new SemaphoreSlim(Options.MaxConcurrentIngestions, Options.MaxConcurrentIngestions);
        _contract = string.Create(CultureInfo.InvariantCulture, $"v1:chunk={Options.MaxChunkCharacters}:overlap={Options.ChunkOverlapCharacters}:vector={vectors?.StorageContract ?? "none"}:model={embeddings?.ModelIdentity ?? "none"}");
    }

    public SearchStoreOptions Options { get; }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@name, 0))"))
        {
            Parameter(command, "name", "BlueTusk.Search:" + Options.Schema);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_vectors is not null)
        {
            await _vectors.ValidateInstallationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        }

        await using (var command = Command(connection, transaction, $"""
            CREATE SCHEMA IF NOT EXISTS {_schema};
            CREATE TABLE IF NOT EXISTS {_schema}.storage_metadata (
                singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton), storage_version integer NOT NULL, contract text NOT NULL);
            """))
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = Command(connection, transaction, $"INSERT INTO {_schema}.storage_metadata (storage_version, contract) VALUES (@version, @contract) ON CONFLICT (singleton) DO NOTHING"))
        {
            Parameter(command, "version", CurrentStorageVersion, DbType.Int32);
            Parameter(command, "contract", _contract);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = Command(connection, transaction, $"SELECT storage_version, contract FROM {_schema}.storage_metadata WHERE singleton"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != CurrentStorageVersion || !string.Equals(reader.GetString(1), _contract, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Installed search storage, chunking, embedding model or vector contract differs from this store.");
            }
        }

        var embeddingColumn = _vectors is null ? string.Empty : ", embedding " + _vectors.ColumnTypeSql;
        await using (var command = Command(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_schema}.documents (
                tenant text COLLATE "C" NOT NULL,
                index_name text COLLATE "C" NOT NULL,
                document_id text COLLATE "C" NOT NULL,
                source_version bigint NOT NULL CHECK (source_version > 0),
                fingerprint char(64) NOT NULL,
                deleted boolean NOT NULL,
                title text NOT NULL,
                metadata jsonb NOT NULL,
                principals jsonb NOT NULL,
                is_public boolean NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (tenant, index_name, document_id));
            CREATE TABLE IF NOT EXISTS {_schema}.chunks (
                tenant text COLLATE "C" NOT NULL,
                index_name text COLLATE "C" NOT NULL,
                document_id text COLLATE "C" NOT NULL,
                ordinal integer NOT NULL CHECK (ordinal >= 0),
                source_version bigint NOT NULL CHECK (source_version > 0),
                content text NOT NULL,
                terms tsvector NOT NULL
                {embeddingColumn},
                PRIMARY KEY (tenant, index_name, document_id, ordinal),
                FOREIGN KEY (tenant, index_name, document_id) REFERENCES {_schema}.documents);
            CREATE INDEX IF NOT EXISTS chunks_terms ON {_schema}.chunks USING gin (terms);
            CREATE INDEX IF NOT EXISTS documents_principals ON {_schema}.documents USING gin (principals);
            CREATE TABLE IF NOT EXISTS {_schema}.queries (
                query_id uuid PRIMARY KEY,
                tenant text NOT NULL,
                index_name text NOT NULL,
                scope_fingerprint char(64) NOT NULL,
                expires_at timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS queries_expiry ON {_schema}.queries (expires_at);
            CREATE INDEX IF NOT EXISTS queries_scope_expiry ON {_schema}.queries (tenant, index_name, expires_at);
            CREATE TABLE IF NOT EXISTS {_schema}.query_results (
                query_id uuid NOT NULL REFERENCES {_schema}.queries ON DELETE CASCADE,
                rank integer NOT NULL CHECK (rank > 0),
                document_id text COLLATE "C" NOT NULL,
                ordinal integer NOT NULL,
                source_version bigint NOT NULL,
                score double precision NOT NULL,
                PRIMARY KEY (query_id, rank));
            """))
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SearchIngestionResult> UpsertAsync(SearchDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ThrowIfDisposed();
        ValidateDocument(document);
        if (!await _ingestionSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new SearchBackpressureException();
        }

        try
        {
            ThrowIfDisposed();
            var chunks = SearchChunker.Chunk(document.Content, Options.MaxChunkCharacters, Options.ChunkOverlapCharacters, Options.MaxChunksPerDocument);
            var fingerprint = Fingerprint(document);
            // Avoid paying for embeddings on known replays; the transaction below rechecks the fence after concurrent work.
            var existing = await InspectVersionAsync(document.Scope, document.Id, document.Version, fingerprint, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            var embeddings = await EmbedAsync(document.Scope, chunks.Select(static x => x.Content).ToArray(), cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var fence = await FenceAsync(connection, transaction, document.Scope, document.Id, document.Version, fingerprint, deleted: false, document, cancellationToken).ConfigureAwait(false);
            if (fence is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return fence;
            }

            await DeleteChunksAsync(connection, transaction, document.Scope, document.Id, cancellationToken).ConfigureAwait(false);
            foreach (var chunk in chunks.Chunk(128))
            {
                await using var command = Command(connection, transaction, string.Empty);
                var sql = new StringBuilder($"INSERT INTO {_schema}.chunks (tenant, index_name, document_id, ordinal, source_version, content, terms");
                if (_vectors is not null)
                {
                    sql.Append(", embedding");
                }

                sql.Append(") VALUES ");
                CommonParameters(command, document.Scope, document.Id);
                Parameter(command, "version", document.Version, DbType.Int64);
                Parameter(command, "title", document.Title);
                for (var i = 0; i < chunk.Length; i++)
                {
                    if (i != 0)
                    {
                        sql.Append(',');
                    }

                    sql.Append(CultureInfo.InvariantCulture, $"(@tenant,@index,@id,@ordinal{i},@version,@content{i},setweight(to_tsvector('pg_catalog.simple'::regconfig,@title),'A') || setweight(to_tsvector('pg_catalog.simple'::regconfig,@content{i}),'D')");
                    Parameter(command, $"ordinal{i}", chunk[i].Ordinal, DbType.Int32);
                    Parameter(command, $"content{i}", chunk[i].Content);
                    if (_vectors is not null)
                    {
                        sql.Append(',').Append(_vectors.ParameterSql($"@embedding{i}"));
                        Parameter(command, $"embedding{i}", _vectors.Encode(embeddings[chunk[i].Ordinal].Span));
                    }

                    sql.Append(')');
                }

                command.CommandText = sql.ToString();
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SearchIngestionResult(SearchIngestionStatus.Applied, document.Version, chunks.Count);
        }
        finally
        {
            _ingestionSlots.Release();
        }
    }

    /// <summary>Retains a versioned deletion fence so delayed older ingestion cannot resurrect a document.</summary>
    public async ValueTask<SearchIngestionResult> DeleteAsync(string tenant, string index, string id, long version, CancellationToken cancellationToken = default)
    {
        var scope = new SearchScope(tenant, index);
        SearchValidation.Key(id, nameof(id), 512);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var fence = await FenceAsync(connection, transaction, scope, id, version, SearchValidation.Hash("deleted"), deleted: true, null, cancellationToken).ConfigureAwait(false);
        if (fence is null)
        {
            await DeleteChunksAsync(connection, transaction, scope, id, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return fence ?? new SearchIngestionResult(SearchIngestionStatus.Applied, version, 0);
    }

    public async ValueTask<int> PruneExpiredQueriesAsync(int maxQueries = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxQueries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxQueries, 10_000);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            WITH expired AS (
                SELECT query_id FROM {_schema}.queries WHERE expires_at <= clock_timestamp()
                ORDER BY expires_at LIMIT @count FOR UPDATE SKIP LOCKED)
            DELETE FROM {_schema}.queries q USING expired e WHERE q.query_id = e.query_id
            """);
        Parameter(command, "count", maxQueries, DbType.Int32);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        for (var i = 0; i < Options.MaxConcurrentIngestions; i++)
        {
            await _ingestionSlots.WaitAsync().ConfigureAwait(false);
        }

        _ingestionSlots.Dispose();
        if (_ownership is SearchDataSourceOwnership.Owned)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<SearchIngestionResult?> InspectVersionAsync(SearchScope scope, string id, long version, string fingerprint, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT source_version, fingerprint FROM {_schema}.documents WHERE tenant = @tenant AND index_name = @index AND document_id = @id");
        CommonParameters(command, scope, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return CompareVersion(id, version, fingerprint, reader.GetInt64(0), reader.GetString(1));
        }

        return null;
    }

    private async ValueTask<SearchIngestionResult?> FenceAsync(DbConnection connection, DbTransaction transaction, SearchScope scope, string id, long version, string fingerprint, bool deleted, SearchDocument? document, CancellationToken cancellationToken)
    {
        await using (var command = Command(connection, transaction, $"""
            INSERT INTO {_schema}.documents (tenant,index_name,document_id,source_version,fingerprint,deleted,title,metadata,principals,is_public)
            VALUES (@tenant,@index,@id,@version,@fingerprint,@deleted,@title,CAST(@metadata AS jsonb),CAST(@principals AS jsonb),@public)
            ON CONFLICT (tenant,index_name,document_id) DO NOTHING RETURNING source_version
            """))
        {
            CommonParameters(command, scope, id);
            Parameter(command, "version", version, DbType.Int64);
            Parameter(command, "fingerprint", fingerprint);
            Parameter(command, "deleted", deleted, DbType.Boolean);
            DocumentParameters(command, document);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return null;
            }
        }

        await using (var command = Command(connection, transaction, $"SELECT source_version, fingerprint FROM {_schema}.documents WHERE tenant = @tenant AND index_name = @index AND document_id = @id FOR UPDATE"))
        {
            CommonParameters(command, scope, id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The search version fence disappeared during ingestion.");
            }

            var result = CompareVersion(id, version, fingerprint, reader.GetInt64(0), reader.GetString(1));
            if (result is not null)
            {
                return result;
            }
        }

        await using (var command = Command(connection, transaction, $"UPDATE {_schema}.documents SET source_version=@version,fingerprint=@fingerprint,deleted=@deleted,title=@title,metadata=CAST(@metadata AS jsonb),principals=CAST(@principals AS jsonb),is_public=@public,updated_at=clock_timestamp() WHERE tenant=@tenant AND index_name=@index AND document_id=@id"))
        {
            CommonParameters(command, scope, id);
            Parameter(command, "version", version, DbType.Int64);
            Parameter(command, "fingerprint", fingerprint);
            Parameter(command, "deleted", deleted, DbType.Boolean);
            DocumentParameters(command, document);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static SearchIngestionResult? CompareVersion(string id, long requestedVersion, string fingerprint, long currentVersion, string currentFingerprint)
    {
        if (requestedVersion < currentVersion)
        {
            return new SearchIngestionResult(SearchIngestionStatus.StaleIgnored, currentVersion, 0);
        }

        if (requestedVersion == currentVersion)
        {
            if (!string.Equals(fingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                throw new SearchVersionConflictException(id, requestedVersion);
            }

            return new SearchIngestionResult(SearchIngestionStatus.AlreadyApplied, currentVersion, 0);
        }

        return null;
    }

    private async ValueTask DeleteChunksAsync(DbConnection connection, DbTransaction transaction, SearchScope scope, string id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"DELETE FROM {_schema}.chunks WHERE tenant=@tenant AND index_name=@index AND document_id=@id");
        CommonParameters(command, scope, id);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ReadOnlyMemory<float>[]> EmbedAsync(SearchScope scope, string[] texts, CancellationToken cancellationToken)
    {
        if (_vectors is null)
        {
            return [];
        }

        if (_embeddings is null)
        {
            throw new InvalidOperationException("Vector ingestion requires an embedding provider.");
        }

        var vectors = new ReadOnlyMemory<float>[texts.Length];
        for (var start = 0; start < texts.Length; start += Options.EmbeddingBatchSize)
        {
            var batch = texts.Skip(start).Take(Options.EmbeddingBatchSize).ToArray();
            var generated = _embeddings is IScopedSearchEmbeddingProvider scoped
                ? await scoped.EmbedForScopeAsync(scope, batch, cancellationToken).ConfigureAwait(false)
                : await _embeddings.EmbedAsync(batch, cancellationToken).ConfigureAwait(false);
            if (generated.Count != batch.Length)
            {
                throw new InvalidOperationException("Embedding provider returned a different result count than its input batch.");
            }

            for (var i = 0; i < generated.Count; i++)
            {
                _ = _vectors.Encode(generated[i].Span); // Validate dimensions, finite values and cosine norm before writing.
                vectors[start + i] = generated[i].ToArray();
            }
        }

        return vectors;
    }

    private void ValidateDocument(SearchDocument document)
    {
        if (Encoding.UTF8.GetByteCount(document.Title) > Options.MaxTitleBytes ||
            Encoding.UTF8.GetByteCount(document.MetadataJson) > Options.MaxMetadataBytes ||
            (long)Encoding.UTF8.GetByteCount(document.Content) + Encoding.UTF8.GetByteCount(document.Title) + Encoding.UTF8.GetByteCount(document.MetadataJson) > Options.MaxDocumentBytes)
        {
            throw new ArgumentException("Search document exceeds the title, metadata or total document byte budget.", nameof(document));
        }
    }

    private static string Fingerprint(SearchDocument document) => SearchValidation.Hash(
        document.Title + "\0" + document.Content + "\0" + document.MetadataJson + "\0" + document.Scope.PrincipalsJson + "\0" + document.IsPublic);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private async ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    private DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        return command;
    }

    private static void Parameter(DbCommand command, string name, object? value, DbType type = DbType.String)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static void CommonParameters(DbCommand command, SearchScope scope, string id)
    {
        Parameter(command, "tenant", scope.Tenant);
        Parameter(command, "index", scope.Index);
        Parameter(command, "id", id);
    }

    private static void DocumentParameters(DbCommand command, SearchDocument? document)
    {
        Parameter(command, "title", document?.Title ?? string.Empty);
        Parameter(command, "metadata", document?.MetadataJson ?? "{}");
        Parameter(command, "principals", document?.Scope.PrincipalsJson ?? "[]");
        Parameter(command, "public", document?.IsPublic ?? false, DbType.Boolean);
    }
}

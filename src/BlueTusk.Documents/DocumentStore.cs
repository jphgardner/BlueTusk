using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace BlueTusk.Documents;

/// <summary>A provider-neutral PostgreSQL JSONB store. Sessions own their transactions; data source ownership is explicit.</summary>
public sealed partial class DocumentStore : IAsyncDisposable
{
    public const int CurrentStorageVersion = 1;
    private readonly DbDataSource _dataSource;
    private readonly DocumentDataSourceOwnership _ownership;
    private int _disposed;

    public DocumentStore(DbDataSource dataSource, DocumentStoreOptions? options = null, DocumentDataSourceOwnership ownership = DocumentDataSourceOwnership.Borrowed)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (!Enum.IsDefined(ownership))
        {
            throw new ArgumentOutOfRangeException(nameof(ownership));
        }

        Options = options ?? new DocumentStoreOptions();
        Options.Validate();
        _dataSource = dataSource;
        _ownership = ownership;
        QuotedSchema = DocumentValidation.Identifier(Options.Schema);
        DocumentsTable = QuotedSchema + ".documents";
        RevisionExpression = "nextval(" + DocumentValidation.Literal(QuotedSchema + ".revisions") + "::regclass)";
    }

    public DocumentStoreOptions Options { get; }
    internal string QuotedSchema { get; }
    internal string DocumentsTable { get; }
    internal string ContentTable => QuotedSchema + ".content";
    internal string ContentLinksTable => QuotedSchema + ".content_links";
    internal string RevisionExpression { get; }

    /// <summary>Idempotently provisions versioned storage. Call during deployment, outside request processing.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var guard = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@schema, 0))"))
        {
            AddParameter(guard, "schema", "BlueTusk.Documents:" + Options.Schema);
            _ = await guard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = Command(connection, transaction, $"""
            CREATE SCHEMA IF NOT EXISTS {QuotedSchema};
            CREATE TABLE IF NOT EXISTS {QuotedSchema}.storage_metadata (
                singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                storage_version integer NOT NULL,
                max_document_bytes integer NOT NULL);
            INSERT INTO {QuotedSchema}.storage_metadata (storage_version, max_document_bytes)
            VALUES ({CurrentStorageVersion}, {Options.MaxDocumentBytes.ToString(CultureInfo.InvariantCulture)})
            ON CONFLICT (singleton) DO NOTHING;
            """))
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = Command(connection, transaction, $"SELECT storage_version, max_document_bytes FROM {QuotedSchema}.storage_metadata WHERE singleton"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != CurrentStorageVersion || reader.GetInt32(1) != Options.MaxDocumentBytes)
            {
                throw new InvalidOperationException("The installed Documents storage version or document byte budget differs from this store configuration.");
            }
        }

        await using (var command = Command(connection, transaction, $"""
            CREATE SEQUENCE IF NOT EXISTS {QuotedSchema}.revisions AS bigint NO CYCLE;
            CREATE TABLE IF NOT EXISTS {DocumentsTable} (
                tenant text COLLATE "C" NOT NULL CHECK (octet_length(tenant) BETWEEN 1 AND 256),
                collection text COLLATE "C" NOT NULL CHECK (octet_length(collection) BETWEEN 1 AND 256),
                id text COLLATE "C" NOT NULL CHECK (octet_length(id) BETWEEN 1 AND 512),
                revision bigint NOT NULL CHECK (revision > 0),
                schema_version integer NOT NULL CHECK (schema_version > 0),
                body jsonb NOT NULL CHECK (jsonb_typeof(body) = 'object' AND octet_length(body::text) <= {Options.MaxDocumentBytes.ToString(CultureInfo.InvariantCulture)}),
                updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (tenant, collection, id));
            CREATE TABLE IF NOT EXISTS {QuotedSchema}.index_definitions (name text PRIMARY KEY, definition text NOT NULL, catalog_definition text NOT NULL);
            CREATE TABLE IF NOT EXISTS {ContentTable} (
                tenant text COLLATE "C" NOT NULL CHECK (octet_length(tenant) BETWEEN 1 AND 256),
                digest bytea NOT NULL CHECK (octet_length(digest) = 32),
                data bytea NOT NULL CHECK (octet_length(data) <= {Options.MaxDocumentBytes.ToString(CultureInfo.InvariantCulture)}),
                PRIMARY KEY (tenant, digest));
            CREATE TABLE IF NOT EXISTS {ContentLinksTable} (
                tenant text COLLATE "C" NOT NULL,
                collection text COLLATE "C" NOT NULL,
                id text COLLATE "C" NOT NULL,
                digest bytea NOT NULL,
                PRIMARY KEY (tenant, collection, id),
                CONSTRAINT content_links_document_fk FOREIGN KEY (tenant, collection, id) REFERENCES {DocumentsTable} (tenant, collection, id) ON DELETE CASCADE,
                CONSTRAINT content_links_content_fk FOREIGN KEY (tenant, digest) REFERENCES {ContentTable} (tenant, digest));
            CREATE INDEX IF NOT EXISTS content_links_digest_idx ON {ContentLinksTable} (tenant, digest);
            """))
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ValidateContentSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public DocumentSession OpenSession(string tenant)
    {
        ThrowIfDisposed();
        DocumentValidation.Key(tenant, nameof(tenant), 256);
        return new DocumentSession(this, tenant);
    }

    public async ValueTask<StoredDocument<T>?> LoadAsync<T>(string tenant, DocumentCollectionDefinition<T> collection, string id, CancellationToken cancellationToken = default)
    {
        ValidateKey(tenant, collection, id);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT body::text, revision, schema_version FROM {DocumentsTable} WHERE tenant = @tenant AND collection = @collection AND id = @id");
        AddParameter(command, "tenant", tenant);
        AddParameter(command, "collection", collection.Name);
        AddParameter(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredDocument<T>(id, Deserialize(reader.GetString(0), collection), reader.GetInt64(1), reader.GetInt32(2))
            : null;
    }

    /// <summary>Reads a bounded keyset page. The cursor is the last returned ID, scoped by caller tenant and collection.</summary>
    public async ValueTask<DocumentPage<T>> ReadPageAsync<T>(string tenant, DocumentCollectionDefinition<T> collection, int pageSize = 100, string? afterId = null, JsonElement? contains = null, CancellationToken cancellationToken = default)
    {
        ValidateKey(tenant, collection, afterId ?? "_");
        if (pageSize <= 0 || pageSize > Options.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var predicate = contains is null ? string.Empty : " AND body @> CAST(@contains AS jsonb)";
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            WITH candidates AS (
                SELECT id, body, revision, schema_version FROM {DocumentsTable}
                WHERE tenant = @tenant AND collection = @collection
                  AND (@after IS NULL OR id > @after COLLATE "C"){predicate}
                ORDER BY id LIMIT @count),
            bounded AS (
                SELECT id, body, revision, schema_version,
                    sum(octet_length(body::text)) OVER (ORDER BY id) AS total_bytes
                FROM candidates)
            SELECT id, CASE WHEN total_bytes <= @bytes THEN body::text ELSE NULL END,
                revision, schema_version FROM bounded ORDER BY id
            """);
        AddParameter(command, "tenant", tenant);
        AddParameter(command, "collection", collection.Name);
        AddParameter(command, "after", afterId, DbType.String);
        AddParameter(command, "count", pageSize + 1, DbType.Int32);
        AddParameter(command, "bytes", Options.MaxPageBytes, DbType.Int64);
        if (contains is not null)
        {
            var json = contains.Value.GetRawText();
            if (System.Text.Encoding.UTF8.GetByteCount(json) > Options.MaxDocumentBytes)
            {
                throw new ArgumentException("Containment filter exceeds the document byte budget.", nameof(contains));
            }

            AddParameter(command, "contains", json);
        }

        var items = new List<StoredDocument<T>>(pageSize);
        var more = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(1) || items.Count == pageSize)
            {
                more = true;
                break;
            }

            items.Add(new StoredDocument<T>(reader.GetString(0), Deserialize(reader.GetString(1), collection), reader.GetInt64(2), reader.GetInt32(3)));
        }

        if (more && items.Count == 0)
        {
            throw new InvalidOperationException("A stored document exceeds the page byte budget.");
        }

        return new DocumentPage<T>(items.AsReadOnly(), more ? items[^1].Id : null);
    }

    /// <summary>Atomically migrates a bounded page from one schema version using optimistic revisions.</summary>
    public async ValueTask<DocumentMigrationPage> MigratePageAsync<T>(string tenant, DocumentCollectionDefinition<T> targetCollection, DocumentMigration<T> migration, int pageSize = 100, string? afterId = null, CancellationToken cancellationToken = default)
    {
        ValidateKey(tenant, targetCollection, afterId ?? "_");
        ArgumentNullException.ThrowIfNull(migration);
        if (migration.ToVersion != targetCollection.SchemaVersion)
        {
            throw new ArgumentException("The migration target version must match the target collection.", nameof(migration));
        }

        if (pageSize <= 0 || pageSize > Math.Min(Options.MaxPageSize, Options.MaxSessionOperations))
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        using var session = OpenSession(tenant);
        var count = 0;
        string? lastId = null;
        var more = false;
        await using (var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = Command(connection, null, $"""
            WITH candidates AS (
                SELECT id, body, revision FROM {DocumentsTable}
                WHERE tenant = @tenant AND collection = @collection AND schema_version = @version
                  AND (@after IS NULL OR id > @after COLLATE "C")
                ORDER BY id LIMIT @count),
            bounded AS (
                SELECT id, body, revision, sum(octet_length(body::text)) OVER (ORDER BY id) AS total_bytes
                FROM candidates)
            SELECT id, CASE WHEN total_bytes <= @bytes THEN body::text ELSE NULL END, revision
            FROM bounded ORDER BY id
            """))
        {
            AddParameter(command, "tenant", tenant);
            AddParameter(command, "collection", targetCollection.Name);
            AddParameter(command, "version", migration.FromVersion, DbType.Int32);
            AddParameter(command, "after", afterId, DbType.String);
            AddParameter(command, "count", pageSize + 1, DbType.Int32);
            AddParameter(command, "bytes", Math.Min(Options.MaxPageBytes, Options.MaxSessionBytes), DbType.Int64);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(1) || count == pageSize)
                {
                    more = true;
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var id = reader.GetString(0);
                using var json = JsonDocument.Parse(reader.GetString(1));
                var transformed = migration.Transform(json.RootElement);
                session.Replace(targetCollection, id, transformed, reader.GetInt64(2));
                lastId = id;
                count++;
            }
        }

        if (more && count == 0)
        {
            throw new InvalidOperationException("A stored document exceeds the migration page byte budget.");
        }

        _ = await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentMigrationPage(count, more ? lastId : null);
    }

    /// <summary>Creates a declared index transactionally; schedule for deployment because PostgreSQL blocks writes during creation.</summary>
    public async ValueTask EnsureIndexAsync(DocumentIndexDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var quotedName = DocumentValidation.Identifier(definition.Name);
        var filter = definition.Collection is null ? string.Empty : " WHERE collection = " + DocumentValidation.Literal(definition.Collection);
        var expression = definition.Kind is DocumentIndexKind.JsonContainment
            ? "USING gin (body jsonb_path_ops)"
            : "USING btree (tenant, (body #>> ARRAY[" + string.Join(',', definition.Path!.Select(DocumentValidation.Literal)) + "]::text[]), id)";
        var sql = $"CREATE INDEX {quotedName} ON {DocumentsTable} {expression}{filter}";
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var guard = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@name, 0))"))
        {
            AddParameter(guard, "name", "BlueTusk.Documents.Index:" + Options.Schema + ":" + definition.Name);
            _ = await guard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var existing = Command(connection, transaction, $"""
            SELECT d.definition, d.catalog_definition,
                pg_get_indexdef(i.indexrelid), i.indisvalid, i.indisready,
                i.indrelid = to_regclass(@table)
            FROM {QuotedSchema}.index_definitions d
            LEFT JOIN pg_index i ON i.indexrelid = to_regclass(@index)
            WHERE d.name = @name
            """))
        {
            AddParameter(existing, "name", definition.Name);
            AddParameter(existing, "table", DocumentsTable);
            AddParameter(existing, "index", QuotedSchema + "." + quotedName);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var text = reader.GetString(0);
                if (!string.Equals(text, sql, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The installed document index has a different definition.");
                }

                if (reader.IsDBNull(2) || !string.Equals(reader.GetString(1), reader.GetString(2), StringComparison.Ordinal) ||
                    !reader.GetBoolean(3) || !reader.GetBoolean(4) || !reader.GetBoolean(5))
                {
                    throw new InvalidOperationException("The declared document index is missing, invalid or changed in the PostgreSQL catalog.");
                }

                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await using (var command = Command(connection, transaction, sql))
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = Command(connection, transaction, $"INSERT INTO {QuotedSchema}.index_definitions SELECT @name, @definition, pg_get_indexdef(to_regclass(@index))"))
        {
            AddParameter(command, "name", definition.Name);
            AddParameter(command, "definition", sql);
            AddParameter(command, "index", QuotedSchema + "." + quotedName);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownership is DocumentDataSourceOwnership.Owned)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    internal DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        return command;
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    internal static void AddParameter(DbCommand command, string name, object? value, DbType type = DbType.String)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static void ValidateKey<T>(string tenant, DocumentCollectionDefinition<T> collection, string id)
    {
        ArgumentNullException.ThrowIfNull(collection);
        DocumentValidation.Key(tenant, nameof(tenant), 256);
        DocumentValidation.Key(id, nameof(id), 512);
    }

    private static T Deserialize<T>(string json, DocumentCollectionDefinition<T> collection) =>
        JsonSerializer.Deserialize(json, collection.JsonTypeInfo) ?? throw new JsonException("Stored document deserialized to null.");
}

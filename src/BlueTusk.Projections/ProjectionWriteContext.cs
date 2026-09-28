using System.Buffers;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace BlueTusk.Projections;

/// <summary>A bounded, transaction-scoped writer. It becomes unusable when its projection callback ends.</summary>
public sealed class ProjectionWriteContext
{
    private readonly PostgreSqlProjectionsOptions _options;
    private readonly string _schema;
    private readonly ProjectionIdentity _identity;
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private bool _closed;
    private int _writes;
    private int _invalidations;
    private long _writeBytes;
    private bool _boundExceeded;
    private readonly HashSet<(string Tenant, ProjectionDependency Dependency, string After)> _pendingPages = [];

    internal ProjectionWriteContext(DbConnection connection, DbTransaction transaction, ProjectionIdentity identity,
        PostgreSqlProjectionsOptions options)
    {
        _connection = connection;
        _transaction = transaction;
        _identity = identity;
        _options = options;
        _schema = '"' + options.Schema + '"';
    }

    public DbConnection Connection { get { EnsureActive(); return _connection; } }
    public DbTransaction Transaction { get { EnsureActive(); return _transaction; } }
    internal int WriteCount => _writes;
    internal int InvalidationCount => _invalidations;
    internal long WriteBytes => _writeBytes;

    /// <summary>Bulk-upsert documents and reconcile dependencies with three SQL commands, bounded by count and bytes.</summary>
    public async ValueTask UpsertManyAsync(IReadOnlyList<ProjectionDocumentWrite> documents,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0 || documents.Count > _options.MaximumWriteBatchSize)
        {
            throw BoundExceeded("The document write batch must be nonempty and within its configured count bound.");
        }

        long bytes = 0;
        var keys = new HashSet<(string Tenant, string Key)>();
        foreach (var document in documents)
        {
            ArgumentNullException.ThrowIfNull(document);
            ValidateDocument(document.TenantId, document.Key, document.Payload);
            ArgumentNullException.ThrowIfNull(document.Dependencies);
            if (!keys.Add((document.TenantId, document.Key)))
            {
                throw new ArgumentException("A bulk write cannot repeat a tenant/document key.", nameof(documents));
            }

            bytes += document.Payload.Length;
            if (bytes > _options.MaximumWriteBatchBytes || document.Dependencies.Count > _options.MaximumDependenciesPerDocument)
            {
                throw BoundExceeded("A bulk document write exceeds its configured byte or dependency bound.");
            }

            var dependencies = new HashSet<ProjectionDependency>();
            foreach (var dependency in document.Dependencies)
            {
                ArgumentNullException.ThrowIfNull(dependency);
                if (!dependencies.Add(dependency))
                {
                    throw new ArgumentException("Dependencies must be unique within each document.", nameof(documents));
                }
            }

            CountWrite(document.Payload.Length);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var document in documents)
            {
                writer.WriteStartObject();
                writer.WriteString("tenant_id", document.TenantId);
                writer.WriteString("document_key", document.Key);
                writer.WriteBase64String("payload", document.Payload.Span);
                writer.WriteStartArray("dependencies");
                foreach (var dependency in document.Dependencies)
                {
                    writer.WriteStartObject();
                    writer.WriteString("table_id", dependency.TableId);
                    writer.WriteString("key_id", dependency.KeyId);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        var batch = Encoding.UTF8.GetString(buffer.WrittenSpan);
        await ExecuteAsync($"""
            INSERT INTO {_schema}.documents(projection, version, tenant_id, document_key, payload)
            SELECT @projection, @version, i.tenant_id, i.document_key, decode(i.payload, 'base64')
            FROM jsonb_to_recordset(CAST(@batch AS jsonb)) AS i(tenant_id text, document_key text, payload text)
            ON CONFLICT(projection, version, tenant_id, document_key) DO UPDATE SET payload = EXCLUDED.payload
            """, cancellationToken, ("batch", batch)).ConfigureAwait(false);
        await ExecuteAsync($"""
            DELETE FROM {_schema}.dependencies d USING jsonb_to_recordset(CAST(@batch AS jsonb)) AS i(tenant_id text, document_key text, dependencies jsonb)
            WHERE d.projection = @projection AND d.version = @version AND d.tenant_id = i.tenant_id AND d.document_key = i.document_key
                AND NOT EXISTS (
                    SELECT 1 FROM jsonb_to_recordset(i.dependencies) AS wanted(table_id text, key_id text)
                    WHERE wanted.table_id = d.table_id AND wanted.key_id = d.key_id)
            """, cancellationToken, ("batch", batch)).ConfigureAwait(false);
        await ExecuteAsync($"""
            INSERT INTO {_schema}.dependencies(projection, version, tenant_id, document_key, table_id, key_id)
            SELECT @projection, @version, i.tenant_id, i.document_key, d.table_id, d.key_id
            FROM jsonb_to_recordset(CAST(@batch AS jsonb)) AS i(tenant_id text, document_key text, dependencies jsonb)
            CROSS JOIN LATERAL jsonb_to_recordset(i.dependencies) AS d(table_id text, key_id text)
            ON CONFLICT(projection, version, tenant_id, document_key, table_id, key_id) DO NOTHING
            """, cancellationToken, ("batch", batch)).ConfigureAwait(false);
    }

    public ValueTask UpsertJsonAsync<T>(string tenantId, string key, T value, JsonTypeInfo<T> jsonTypeInfo,
        IReadOnlyList<ProjectionDependency> dependencies, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return UpsertAsync(tenantId, key, JsonSerializer.SerializeToUtf8Bytes(value, jsonTypeInfo), dependencies, cancellationToken);
    }

    public async ValueTask UpsertAsync(string tenantId, string key, ReadOnlyMemory<byte> payload,
        IReadOnlyList<ProjectionDependency> dependencies, CancellationToken cancellationToken = default)
    {
        ValidateDocument(tenantId, key, payload);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (dependencies.Count > _options.MaximumDependenciesPerDocument)
        {
            throw BoundExceeded("The document exceeds the configured dependency count.");
        }

        var unique = new HashSet<ProjectionDependency>();
        foreach (var dependency in dependencies)
        {
            ArgumentNullException.ThrowIfNull(dependency);
            if (!unique.Add(dependency))
            {
                throw new ArgumentException("A document's dependencies must be unique.", nameof(dependencies));
            }
        }

        CountWrite(payload.Length);
        await ExecuteAsync($"""
            INSERT INTO {_schema}.documents (projection, version, tenant_id, document_key, payload)
            VALUES (@projection, @version, @tenant, @key, @payload)
            ON CONFLICT (projection, version, tenant_id, document_key) DO UPDATE SET payload = EXCLUDED.payload
            """, cancellationToken, ("tenant", tenantId), ("key", key), ("payload", payload.ToArray())).ConfigureAwait(false);
        await ExecuteAsync($"""
            DELETE FROM {_schema}.dependencies WHERE projection = @projection AND version = @version AND tenant_id = @tenant AND document_key = @key
            """, cancellationToken, ("tenant", tenantId), ("key", key)).ConfigureAwait(false);
        if (dependencies.Count > 0)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                foreach (var dependency in dependencies)
                {
                    writer.WriteStartObject();
                    writer.WriteString("table_id", dependency.TableId);
                    writer.WriteString("key_id", dependency.KeyId);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            await ExecuteAsync($"""
                INSERT INTO {_schema}.dependencies (projection, version, tenant_id, document_key, table_id, key_id)
                SELECT @projection, @version, @tenant, @key, i.table_id, i.key_id
                FROM jsonb_to_recordset(CAST(@dependencies AS jsonb)) AS i(table_id text, key_id text)
                """, cancellationToken, ("tenant", tenantId), ("key", key),
                ("dependencies", Encoding.UTF8.GetString(buffer.WrittenSpan))).ConfigureAwait(false);
        }
    }

    public async ValueTask DeleteAsync(string tenantId, string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantId, key);
        CountWrite(0);
        await ExecuteAsync($"""
            DELETE FROM {_schema}.documents WHERE projection = @projection AND version = @version AND tenant_id = @tenant AND document_key = @key
            """, cancellationToken, ("tenant", tenantId), ("key", key)).ConfigureAwait(false);
    }

    /// <summary>Keyset-page invalidated outputs. Consume every page or fail the entire CDC transaction.</summary>
    public async ValueTask<ProjectionDependencyPage> ReadDependentsAsync(string tenantId, ProjectionDependency dependency,
        string? afterDocumentKey = null, int maximumDocuments = 256, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ProjectionValidation.Key(tenantId, nameof(tenantId));
        ArgumentNullException.ThrowIfNull(dependency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDocuments);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumDocuments, _options.MaximumDependencyPageSize);
        if (afterDocumentKey is not null)
        {
            ProjectionValidation.Key(afterDocumentKey, nameof(afterDocumentKey));
        }

        await using var command = Command($"""
            SELECT document_key FROM {_schema}.dependencies
            WHERE projection = @projection AND version = @version AND tenant_id = @tenant
                AND table_id = @table AND key_id = @dependency_key AND document_key > @after COLLATE "C"
            ORDER BY document_key COLLATE "C" LIMIT @limit
            """, ("tenant", tenantId), ("table", dependency.TableId), ("dependency_key", dependency.KeyId),
            ("after", afterDocumentKey ?? string.Empty), ("limit", checked(maximumDocuments + 1)));
        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0));
        }

        var hasMore = keys.Count > maximumDocuments;
        if (hasMore)
        {
            keys.RemoveAt(keys.Count - 1);
        }

        if (afterDocumentKey is not null)
        {
            _pendingPages.Remove((tenantId, dependency, afterDocumentKey));
        }

        if (hasMore)
        {
            _pendingPages.Add((tenantId, dependency, keys[^1]));
        }

        _invalidations = checked(_invalidations + keys.Count);
        if (_invalidations > _options.MaximumInvalidationsPerTransaction)
        {
            throw BoundExceeded("Dependency fan-out exceeds the configured transaction invalidation bound; the checkpoint must not advance.");
        }

        return new ProjectionDependencyPage(keys, hasMore ? keys[^1] : null);
    }

    /// <summary>Persist a table's source image so joins use the same committed CDC history as the checkpoint.</summary>
    public async ValueTask UpsertSourcesAsync(IReadOnlyList<ProjectionSourceWrite> sources, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(sources);
        ValidateSourceBatchCount(sources.Count);
        var keys = new HashSet<(string Tenant, ProjectionDependency Key)>();
        long bytes = 0;
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(source.SourceKey);
            ValidateDocument(source.TenantId, source.SourceKey.KeyId, source.Payload);
            if (!keys.Add((source.TenantId, source.SourceKey))) { throw new ArgumentException("Source keys must be unique within a bulk batch.", nameof(sources)); }
            bytes += source.Payload.Length;
            if (bytes > _options.MaximumWriteBatchBytes) { throw BoundExceeded("The source mirror batch exceeds its byte bound."); }
            CountWrite(source.Payload.Length);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var source in sources)
            {
                WriteSourceKey(writer, source.TenantId, source.SourceKey);
                writer.WriteBase64String("payload", source.Payload.Span);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        await ExecuteAsync($"""
            INSERT INTO {_schema}.source_rows(projection, version, tenant_id, table_id, key_id, payload)
            SELECT @projection, @version, i.tenant_id, i.table_id, i.key_id, decode(i.payload, 'base64')
            FROM jsonb_to_recordset(CAST(@batch AS jsonb)) AS i(tenant_id text, table_id text, key_id text, payload text)
            ON CONFLICT(projection, version, tenant_id, table_id, key_id) DO UPDATE SET payload = EXCLUDED.payload
            """, cancellationToken, ("batch", Encoding.UTF8.GetString(buffer.WrittenSpan))).ConfigureAwait(false);
    }

    public async ValueTask DeleteSourcesAsync(IReadOnlyList<ProjectionSourceDelete> sources, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(sources);
        ValidateSourceBatchCount(sources.Count);
        var keys = new HashSet<(string Tenant, ProjectionDependency Key)>();
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(source.SourceKey);
            ValidateKey(source.TenantId, source.SourceKey.KeyId);
            if (!keys.Add((source.TenantId, source.SourceKey))) { throw new ArgumentException("Source keys must be unique within a bulk batch.", nameof(sources)); }
            CountWrite(0);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var source in sources)
            {
                WriteSourceKey(writer, source.TenantId, source.SourceKey);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        await ExecuteAsync($"""
            DELETE FROM {_schema}.source_rows d USING jsonb_to_recordset(CAST(@batch AS jsonb)) AS i(tenant_id text, table_id text, key_id text)
            WHERE d.projection = @projection AND d.version = @version AND d.tenant_id = i.tenant_id AND d.table_id = i.table_id AND d.key_id = i.key_id
            """, cancellationToken, ("batch", Encoding.UTF8.GetString(buffer.WrittenSpan))).ConfigureAwait(false);
    }

    private void ValidateSourceBatchCount(int count)
    {
        if (count == 0 || count > _options.MaximumWriteBatchSize) { throw BoundExceeded("Source mirror batches must be nonempty and within their count bound."); }
    }

    private static void WriteSourceKey(Utf8JsonWriter writer, string tenantId, ProjectionDependency key)
    {
        writer.WriteStartObject();
        writer.WriteString("tenant_id", tenantId);
        writer.WriteString("table_id", key.TableId);
        writer.WriteString("key_id", key.KeyId);
    }

    /// <summary>Persist a table's source image so joins use the same committed CDC history as the checkpoint.</summary>
    public async ValueTask UpsertSourceAsync(string tenantId, ProjectionDependency sourceKey, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceKey);
        ValidateDocument(tenantId, sourceKey.KeyId, payload);
        CountWrite(payload.Length);
        await ExecuteAsync($"""
            INSERT INTO {_schema}.source_rows (projection, version, tenant_id, table_id, key_id, payload)
            VALUES (@projection, @version, @tenant, @table, @key, @payload)
            ON CONFLICT (projection, version, tenant_id, table_id, key_id) DO UPDATE SET payload = EXCLUDED.payload
            """, cancellationToken, ("tenant", tenantId), ("table", sourceKey.TableId), ("key", sourceKey.KeyId),
            ("payload", payload.ToArray())).ConfigureAwait(false);
    }

    public async ValueTask DeleteSourceAsync(string tenantId, ProjectionDependency sourceKey, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ProjectionValidation.Key(tenantId, nameof(tenantId));
        ArgumentNullException.ThrowIfNull(sourceKey);
        CountWrite(0);
        await ExecuteAsync($"""
            DELETE FROM {_schema}.source_rows WHERE projection = @projection AND version = @version AND tenant_id = @tenant AND table_id = @table AND key_id = @key
            """, cancellationToken, ("tenant", tenantId), ("table", sourceKey.TableId), ("key", sourceKey.KeyId)).ConfigureAwait(false);
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReadSourceAsync(string tenantId, ProjectionDependency sourceKey,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ProjectionValidation.Key(tenantId, nameof(tenantId));
        ArgumentNullException.ThrowIfNull(sourceKey);
        await using var command = Command($"""
            SELECT payload FROM {_schema}.source_rows WHERE projection = @projection AND version = @version AND tenant_id = @tenant AND table_id = @table AND key_id = @key
            """, ("tenant", tenantId), ("table", sourceKey.TableId), ("key", sourceKey.KeyId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is byte[] bytes ? new ReadOnlyMemory<byte>(bytes) : (ReadOnlyMemory<byte>?)null;
    }

    /// <summary>Apply an exact decimal aggregate delta alongside source mirrors, documents, and checkpoint.</summary>
    public async ValueTask AddAggregateAsync(string tenantId, string groupKey, string metric, decimal delta,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantId, groupKey);
        ProjectionValidation.Key(metric, nameof(metric));
        CountWrite(0);
        await ExecuteAsync($"""
            INSERT INTO {_schema}.aggregates (projection, version, tenant_id, group_key, metric, value)
            VALUES (@projection, @version, @tenant, @group_key, @metric, @delta)
            ON CONFLICT (projection, version, tenant_id, group_key, metric) DO UPDATE SET value = {_schema}.aggregates.value + EXCLUDED.value
            """, cancellationToken, ("tenant", tenantId), ("group_key", groupKey), ("metric", metric), ("delta", delta)).ConfigureAwait(false);
    }

    public async ValueTask<decimal> ReadAggregateAsync(string tenantId, string groupKey, string metric,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantId, groupKey);
        ProjectionValidation.Key(metric, nameof(metric));
        await using var command = Command($"""
            SELECT value FROM {_schema}.aggregates WHERE projection = @projection AND version = @version AND tenant_id = @tenant AND group_key = @group_key AND metric = @metric
            """, ("tenant", tenantId), ("group_key", groupKey), ("metric", metric));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? reader.GetDecimal(0) : 0;
    }

    internal void Close() => _closed = true;

    internal void EnsureComplete()
    {
        EnsureActive();
        if (_boundExceeded || _pendingPages.Count != 0)
        {
            throw new ProjectionBoundExceededException("A projection bound was exceeded or dependency pagination was left incomplete; the transaction cannot checkpoint.");
        }
    }

    private void EnsureActive()
    {
        if (_closed)
        {
            throw new InvalidOperationException("This projection context's callback has finished.");
        }
    }

    private void ValidateKey(string tenantId, string key)
    {
        EnsureActive();
        ProjectionValidation.Key(tenantId, nameof(tenantId));
        ProjectionValidation.Key(key, nameof(key));
    }

    private void ValidateDocument(string tenantId, string key, ReadOnlyMemory<byte> payload)
    {
        ValidateKey(tenantId, key);
        if (payload.IsEmpty || payload.Length > _options.MaximumDocumentBytes)
        {
            throw BoundExceeded("The document payload is empty or exceeds the configured byte bound.");
        }
    }

    private void CountWrite(int bytes)
    {
        EnsureActive();
        if (++_writes > _options.MaximumWriteOperationsPerTransaction ||
            (_writeBytes = checked(_writeBytes + bytes)) > _options.MaximumTransactionBytes)
        {
            throw BoundExceeded("Projection output exceeds the configured transaction operation or byte bound.");
        }
    }

    private ProjectionBoundExceededException BoundExceeded(string message)
    {
        _boundExceeded = true;
        return new ProjectionBoundExceededException(message);
    }

    private DbCommand Command(string sql, params (string Name, object Value)[] additional)
    {
        EnsureActive();
        var parameters = new (string Name, object Value)[additional.Length + 2];
        parameters[0] = ("projection", _identity.Name);
        parameters[1] = ("version", _identity.Version);
        additional.CopyTo(parameters, 2);
        return ProjectionSql.Command(_connection, _transaction, _options.CommandTimeoutSeconds, sql, parameters);
    }

    private async ValueTask ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

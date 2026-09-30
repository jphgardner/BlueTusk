using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BlueTusk.Documents;

/// <summary>A bounded write unit of work. Stage one mutation per key and commit all writes with optimistic revision checks.</summary>
public sealed class DocumentSession : IDisposable
{
    private readonly DocumentStore _store;
    private readonly Dictionary<(string Collection, string Id), Mutation> _pending = [];
    private readonly object _gate = new();
    private long _bytes;
    private bool _saving;
    private bool _disposed;

    internal DocumentSession(DocumentStore store, string tenant)
    {
        _store = store;
        Tenant = tenant;
    }

    public string Tenant { get; }
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public void Insert<T>(DocumentCollectionDefinition<T> collection, string id, T value) =>
        Stage(Serialize(collection, id, value, null));

    public void Replace<T>(DocumentCollectionDefinition<T> collection, string id, T value, long expectedRevision)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        Stage(Serialize(collection, id, value, expectedRevision));
    }

    public void Delete<T>(DocumentCollectionDefinition<T> collection, string id, long expectedRevision)
    {
        Validate(collection, id, expectedRevision);
        Stage(new Mutation(collection.Name, id, MutationKind.Delete, expectedRevision, collection.SchemaVersion, null, [], 0));
    }

    /// <summary>Applies set/remove operations in order. Missing intermediate objects are not synthesized by PostgreSQL jsonb_set.</summary>
    public void Patch<T>(DocumentCollectionDefinition<T> collection, string id, long expectedRevision, IReadOnlyList<DocumentPatch> operations)
    {
        Validate(collection, id, expectedRevision);
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count is 0 or > 128)
        {
            throw new ArgumentException("A patch requires between 1 and 128 operations.", nameof(operations));
        }

        var patches = operations.ToArray();
        long bytes = 0;
        foreach (var patch in patches)
        {
            ArgumentNullException.ThrowIfNull(patch);
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(patch.PathJson) + (patch.JsonValue is null ? 0 : Encoding.UTF8.GetByteCount(patch.JsonValue)));
        }

        if (bytes > _store.Options.MaxDocumentBytes)
        {
            throw new ArgumentException("Patch exceeds the document byte budget.", nameof(operations));
        }

        Stage(new Mutation(collection.Name, id, MutationKind.Patch, expectedRevision, collection.SchemaVersion, null, patches, bytes));
    }

    public void Clear()
    {
        lock (_gate)
        {
            AssertMutable();
            _pending.Clear();
            _bytes = 0;
        }
    }

    /// <summary>Owns the transaction and rolls back every staged mutation on conflict. Results become visible only after commit succeeds.</summary>
    public async ValueTask<IReadOnlyList<DocumentWriteResult>> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Mutation[] mutations;
        lock (_gate)
        {
            AssertMutable();
            _saving = true;
            // Consistent lock acquisition order reduces deadlocks across sessions with overlapping keys.
            mutations = _pending.Values.OrderBy(static x => x.Collection, StringComparer.Ordinal).ThenBy(static x => x.Id, StringComparer.Ordinal).ToArray();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mutations.Length == 0)
            {
                return Array.Empty<DocumentWriteResult>();
            }

            await using var connection = await _store.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            var results = new List<DocumentWriteResult>(mutations.Length);
            foreach (var chunk in mutations.Chunk(_store.Options.CommandsPerBatch))
            {
                if (connection.CanCreateBatch)
                {
                    await ExecuteBatchAsync(connection, transaction, chunk, results, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    foreach (var mutation in chunk)
                    {
                        await ExecuteSingleAsync(connection, transaction, mutation, results, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _pending.Clear();
                _bytes = 0;
            }

            return results.AsReadOnly();
        }
        finally
        {
            lock (_gate)
            {
                _saving = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_saving)
            {
                throw new InvalidOperationException("Cannot dispose a session while it is saving.");
            }

            _disposed = true;
            _pending.Clear();
            _bytes = 0;
        }
    }

    private async ValueTask ExecuteBatchAsync(DbConnection connection, DbTransaction transaction, Mutation[] chunk, List<DocumentWriteResult> results, CancellationToken cancellationToken)
    {
        await using var batch = connection.CreateBatch();
        batch.Transaction = transaction;
        batch.Timeout = _store.Options.CommandTimeoutSeconds;
        using var parameterFactory = connection.CreateCommand();
        foreach (var mutation in chunk)
        {
            var prepared = Prepare(mutation);
            var command = batch.CreateBatchCommand();
            command.CommandText = prepared.Sql;
            foreach (var input in prepared.Parameters)
            {
                var parameter = command.CanCreateParameter ? command.CreateParameter() : parameterFactory.CreateParameter();
                parameter.ParameterName = input.Name;
                parameter.DbType = input.Type;
                parameter.Value = input.Value;
                command.Parameters.Add(parameter);
            }

            batch.BatchCommands.Add(command);
        }

        Mutation? conflict = null;
        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var i = 0; i < chunk.Length; i++)
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    results.Add(Result(chunk[i], reader.GetInt64(0)));
                }
                else
                {
                    conflict ??= chunk[i];
                }

                if (i + 1 < chunk.Length && !await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The database provider did not return all document batch result sets.");
                }
            }
        }

        if (conflict is not null)
        {
            await ThrowConflictAsync(connection, transaction, conflict, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ExecuteSingleAsync(DbConnection connection, DbTransaction transaction, Mutation mutation, List<DocumentWriteResult> results, CancellationToken cancellationToken)
    {
        var prepared = Prepare(mutation);
        await using var command = _store.Command(connection, transaction, prepared.Sql);
        foreach (var input in prepared.Parameters)
        {
            DocumentStore.AddParameter(command, input.Name, input.Value, input.Type);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
        {
            await ThrowConflictAsync(connection, transaction, mutation, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            results.Add(Result(mutation, Convert.ToInt64(value, CultureInfo.InvariantCulture)));
        }
    }

    private async ValueTask ThrowConflictAsync(DbConnection connection, DbTransaction transaction, Mutation mutation, CancellationToken cancellationToken)
    {
        await using var command = _store.Command(connection, transaction, $"SELECT revision, schema_version FROM {_store.DocumentsTable} WHERE tenant = @tenant AND collection = @collection AND id = @id");
        DocumentStore.AddParameter(command, "tenant", Tenant);
        DocumentStore.AddParameter(command, "collection", mutation.Collection);
        DocumentStore.AddParameter(command, "id", mutation.Id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        long? current = null;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            current = reader.GetInt64(0);
            var version = reader.GetInt32(1);
            if (current == mutation.ExpectedRevision &&
                (version > mutation.SchemaVersion || mutation.Kind is MutationKind.Patch && version != mutation.SchemaVersion))
            {
                throw new DocumentSchemaVersionException(mutation.Collection, mutation.Id, mutation.SchemaVersion, version);
            }
        }

        throw new DocumentConcurrencyException(Tenant, mutation.Collection, mutation.Id, mutation.ExpectedRevision, current);
    }

    private PreparedMutation Prepare(Mutation mutation)
    {
        var parameters = new List<InputParameter>
        {
            new("tenant", Tenant, DbType.String),
            new("collection", mutation.Collection, DbType.String),
            new("id", mutation.Id, DbType.String),
        };
        var predicate = "tenant = @tenant AND collection = @collection AND id = @id AND revision = @expected";
        string sql;
        if (mutation.Kind is MutationKind.Insert)
        {
            sql = $"INSERT INTO {_store.DocumentsTable} (tenant, collection, id, revision, schema_version, body) VALUES (@tenant, @collection, @id, {_store.RevisionExpression}, @version, CAST(@body AS jsonb)) ON CONFLICT (tenant, collection, id) DO NOTHING RETURNING revision";
        }
        else
        {
            parameters.Add(new InputParameter("expected", mutation.ExpectedRevision!.Value, DbType.Int64));
            if (mutation.Kind is MutationKind.Delete)
            {
                sql = $"DELETE FROM {_store.DocumentsTable} WHERE {predicate} RETURNING revision";
            }
            else
            {
                var body = "CAST(@body AS jsonb)";
                if (mutation.Kind is MutationKind.Patch)
                {
                    body = "body";
                    for (var i = 0; i < mutation.Patches.Length; i++)
                    {
                        var patch = mutation.Patches[i];
                        var path = $"ARRAY(SELECT jsonb_array_elements_text(CAST(@path{i} AS jsonb)))";
                        parameters.Add(new InputParameter($"path{i}", patch.PathJson, DbType.String));
                        if (patch.Kind is DocumentPatchKind.Remove)
                        {
                            body = $"({body} #- {path})";
                        }
                        else
                        {
                            parameters.Add(new InputParameter($"value{i}", patch.JsonValue!, DbType.String));
                            body = $"jsonb_set({body}, {path}, CAST(@value{i} AS jsonb), true)";
                        }
                    }
                }

                var versionPredicate = mutation.Kind is MutationKind.Patch ? "schema_version = @version" : "schema_version <= @version";
                sql = $"UPDATE {_store.DocumentsTable} SET body = {body}, revision = {_store.RevisionExpression}, schema_version = @version, updated_at = clock_timestamp() WHERE {predicate} AND {versionPredicate} RETURNING revision";
            }
        }

        if (mutation.Kind is not MutationKind.Delete)
        {
            parameters.Add(new InputParameter("version", mutation.SchemaVersion, DbType.Int32));
        }

        if (mutation.Body is not null)
        {
            parameters.Add(new InputParameter("body", mutation.Body, DbType.String));
        }

        return new PreparedMutation(sql, parameters);
    }

    private Mutation Serialize<T>(DocumentCollectionDefinition<T> collection, string id, T value, long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(value);
        DocumentValidation.Key(id, nameof(id), 512);
        using var stream = new BoundedSerializationStream(_store.Options.MaxDocumentBytes);
        JsonSerializer.Serialize(stream, value, collection.JsonTypeInfo);
        // The parser borrows this exact written range until it is disposed; the returned body string owns its data.
        var bytes = stream.GetBuffer().AsMemory(0, checked((int)stream.Length));
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("A document must serialize to a JSON object.", nameof(value));
        }

        return new Mutation(collection.Name, id, expectedRevision is null ? MutationKind.Insert : MutationKind.Replace, expectedRevision, collection.SchemaVersion, Encoding.UTF8.GetString(bytes.Span), [], bytes.Length);
    }

    private void Stage(Mutation mutation)
    {
        lock (_gate)
        {
            AssertMutable();
            if (_pending.ContainsKey((mutation.Collection, mutation.Id)))
            {
                throw new InvalidOperationException("A session can stage only one operation for each document key; combine patch operations or clear the pending writes.");
            }

            var bytes = checked(_bytes + mutation.Bytes + Encoding.UTF8.GetByteCount(mutation.Id) + Encoding.UTF8.GetByteCount(mutation.Collection));
            if (_pending.Count >= _store.Options.MaxSessionOperations || bytes > _store.Options.MaxSessionBytes)
            {
                throw new InvalidOperationException("The document session count or byte budget has been exceeded.");
            }

            _pending.Add((mutation.Collection, mutation.Id), mutation);
            _bytes = bytes;
        }
    }

    private void AssertMutable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _store.ThrowIfDisposed();
        if (_saving)
        {
            throw new InvalidOperationException("The document session is already saving.");
        }
    }

    private static void Validate<T>(DocumentCollectionDefinition<T> collection, string id, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(collection);
        DocumentValidation.Key(id, nameof(id), 512);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
    }

    private static DocumentWriteResult Result(Mutation mutation, long revision) =>
        new(mutation.Collection, mutation.Id, mutation.Kind is MutationKind.Delete ? null : revision);

    private enum MutationKind { Insert, Replace, Delete, Patch }
    private sealed record Mutation(string Collection, string Id, MutationKind Kind, long? ExpectedRevision, int SchemaVersion, string? Body, DocumentPatch[] Patches, long Bytes);
    private sealed record InputParameter(string Name, object Value, DbType Type);
    private sealed record PreparedMutation(string Sql, List<InputParameter> Parameters);

    private sealed class BoundedSerializationStream(int maxBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }

        private void Check(int count)
        {
            if (Length + count > maxBytes)
            {
                throw new ArgumentException("Serialized document exceeds the document byte budget.");
            }
        }
    }
}

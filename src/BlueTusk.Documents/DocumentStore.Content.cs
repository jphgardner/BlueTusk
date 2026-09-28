using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Documents;

/// <summary>A detached copy of a document's explicitly attached content.</summary>
public sealed class DocumentContent
{
    internal DocumentContent(byte[] bytes, long revision, string sha256)
    {
        Bytes = bytes;
        Revision = revision;
        Sha256 = sha256;
    }

    public ReadOnlyMemory<byte> Bytes { get; }
    public long Revision { get; }
    public string Sha256 { get; }
}

/// <summary>A bounded GC page. A null cursor ends one sweep; start again at null to revisit concurrent changes.</summary>
public sealed class DocumentContentCollectionPage
{
    internal DocumentContentCollectionPage(int examinedCount, int deletedCount, string? nextAfterCursor)
    {
        ExaminedCount = examinedCount;
        DeletedCount = deletedCount;
        NextAfterCursor = nextAfterCursor;
    }

    public int ExaminedCount { get; }
    public int DeletedCount { get; }
    public string? NextAfterCursor { get; }
}

public sealed partial class DocumentStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private async ValueTask ValidateContentSchemaAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT
                EXISTS (SELECT 1 FROM pg_catalog.pg_constraint
                    WHERE conrelid = to_regclass(@content_table) AND contype = 'p'
                      AND convalidated AND conkey = ARRAY[1, 2]::smallint[])
                AND EXISTS (SELECT 1 FROM pg_catalog.pg_constraint
                    WHERE conrelid = to_regclass(@links_table) AND contype = 'p'
                      AND convalidated AND conkey = ARRAY[1, 2, 3]::smallint[])
                AND EXISTS (SELECT 1 FROM pg_catalog.pg_constraint
                    WHERE conrelid = to_regclass(@links_table) AND confrelid = to_regclass(@documents_table)
                      AND contype = 'f' AND convalidated AND confdeltype = 'c'
                      AND conkey = ARRAY[1, 2, 3]::smallint[] AND confkey = ARRAY[1, 2, 3]::smallint[])
                AND EXISTS (SELECT 1 FROM pg_catalog.pg_constraint
                    WHERE conrelid = to_regclass(@links_table) AND confrelid = to_regclass(@content_table)
                      AND contype = 'f' AND convalidated
                      AND conkey = ARRAY[1, 4]::smallint[] AND confkey = ARRAY[1, 2]::smallint[])
            """);
        AddParameter(command, "content_table", ContentTable);
        AddParameter(command, "links_table", ContentLinksTable);
        AddParameter(command, "documents_table", DocumentsTable);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new InvalidOperationException("The installed Documents content schema is missing required keys or foreign keys.");
        }
    }

    /// <summary>
    /// Attaches immutable bytes to an existing document. A tenant shares one stored copy of equal content.
    /// The link and document revision change together; the JSONB body and its query semantics are unchanged.
    /// Initial document insertion and attachment are separate transactions.
    /// </summary>
    public async ValueTask<long> AttachContentAsync<T>(string tenant, DocumentCollectionDefinition<T> collection, string id,
        long expectedRevision, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        ValidateContentKey(tenant, collection, id, expectedRevision);
        if (content.Length > Options.MaxDocumentBytes)
        {
            throw new ArgumentException("Content exceeds the configured document byte budget.", nameof(content));
        }

        // Own the input before the first await: the caller may reuse its buffer while the operation runs.
        var bytes = content.ToArray();
        var digest = SHA256.HashData(bytes);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var revision = await AdvanceRevisionAsync(connection, transaction, tenant, collection.Name, id, expectedRevision, cancellationToken).ConfigureAwait(false);

        // The key-share lock closes the gap with GC between finding a deduplicated row and inserting its FK link.
        // A concurrent GC may win before that lock; retry the insert/lock sequence in that case.
        var secured = false;
        for (var attempt = 0; attempt < 4 && !secured; attempt++)
        {
            var existing = await LockContentAsync(connection, transaction, tenant, digest, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                await using var insert = Command(connection, transaction, $"INSERT INTO {ContentTable} (tenant, digest, data) VALUES (@tenant, @digest, @data) ON CONFLICT (tenant, digest) DO NOTHING");
                AddParameter(insert, "tenant", tenant);
                AddParameter(insert, "digest", digest, DbType.Binary);
                AddParameter(insert, "data", bytes, DbType.Binary);
                _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                existing = await LockContentAsync(connection, transaction, tenant, digest, cancellationToken).ConfigureAwait(false);
            }

            if (existing is not null)
            {
                if (!existing.AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidOperationException("The content digest is already associated with different bytes.");
                }

                secured = true;
            }
        }

        if (!secured)
        {
            throw new InvalidOperationException("Content collection repeatedly raced with attachment; retry the operation.");
        }

        await using (var link = Command(connection, transaction, $"INSERT INTO {ContentLinksTable} AS existing (tenant, collection, id, digest) VALUES (@tenant, @collection, @id, @digest) ON CONFLICT (tenant, collection, id) DO UPDATE SET digest = EXCLUDED.digest WHERE existing.digest IS DISTINCT FROM EXCLUDED.digest"))
        {
            AddParameter(link, "tenant", tenant);
            AddParameter(link, "collection", collection.Name);
            AddParameter(link, "id", id);
            AddParameter(link, "digest", digest, DbType.Binary);
            _ = await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revision;
    }

    /// <summary>Removes the content link, advancing the document revision even if no link exists.</summary>
    public async ValueTask<long> RemoveContentAsync<T>(string tenant, DocumentCollectionDefinition<T> collection, string id,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        ValidateContentKey(tenant, collection, id, expectedRevision);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var revision = await AdvanceRevisionAsync(connection, transaction, tenant, collection.Name, id, expectedRevision, cancellationToken).ConfigureAwait(false);
        await using (var command = Command(connection, transaction, $"DELETE FROM {ContentLinksTable} WHERE tenant = @tenant AND collection = @collection AND id = @id"))
        {
            AddParameter(command, "tenant", tenant);
            AddParameter(command, "collection", collection.Name);
            AddParameter(command, "id", id);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revision;
    }

    /// <summary>Reads the linked bytes and document revision from one database snapshot.</summary>
    public async ValueTask<DocumentContent?> LoadContentAsync<T>(string tenant, DocumentCollectionDefinition<T> collection,
        string id, CancellationToken cancellationToken = default)
    {
        ValidateContentKey(tenant, collection, id);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            SELECT c.data, d.revision, l.digest FROM {DocumentsTable} AS d
            JOIN {ContentLinksTable} AS l USING (tenant, collection, id)
            JOIN {ContentTable} AS c ON c.tenant = l.tenant AND c.digest = l.digest
            WHERE d.tenant = @tenant AND d.collection = @collection AND d.id = @id
            """);
        AddParameter(command, "tenant", tenant);
        AddParameter(command, "collection", collection.Name);
        AddParameter(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var bytes = reader.GetFieldValue<byte[]>(0);
        var digest = reader.GetFieldValue<byte[]>(2);
        if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(digest))
        {
            throw new InvalidOperationException("Stored content does not match its digest.");
        }

        return new DocumentContent(bytes, reader.GetInt64(1), Convert.ToHexStringLower(digest));
    }

    /// <summary>
    /// Examines at most <paramref name="maximumRows"/> content keys in primary-key order and deletes unlinked rows.
    /// Pass the returned cursor into the next call. A null cursor ends one sweep; start another sweep at null to
    /// revisit rows skipped due to concurrent locks or changes. Run this maintenance API outside request processing.
    /// </summary>
    public async ValueTask<DocumentContentCollectionPage> CollectUnusedContentPageAsync(int maximumRows = 1000,
        string? afterCursor = null, CancellationToken cancellationToken = default)
    {
        if (maximumRows is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        }

        var (afterTenant, afterDigest) = DecodeContentCursor(afterCursor);
        var afterPredicate = afterTenant is null ? string.Empty : "WHERE (tenant COLLATE \"C\", digest) > (@after_tenant COLLATE \"C\", @after_digest)";
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            WITH key_window AS MATERIALIZED (
                SELECT tenant, digest FROM {ContentTable}
                {afterPredicate}
                ORDER BY tenant COLLATE "C", digest LIMIT @maximum_rows),
            locked AS MATERIALIZED (
                SELECT candidate.tenant, candidate.digest FROM key_window AS window_key
                CROSS JOIN LATERAL (
                    SELECT c.tenant, c.digest FROM {ContentTable} AS c
                    WHERE c.tenant = window_key.tenant AND c.digest = window_key.digest
                    FOR UPDATE OF c SKIP LOCKED) AS candidate),
            deleted AS (
                DELETE FROM {ContentTable} AS c USING locked AS candidate
                WHERE c.tenant = candidate.tenant AND c.digest = candidate.digest
                  AND NOT EXISTS (
                      SELECT 1 FROM {ContentLinksTable} AS l
                      WHERE l.tenant = c.tenant AND l.digest = c.digest)
                RETURNING 1)
            SELECT (SELECT count(*) FROM key_window), (SELECT count(*) FROM deleted),
                (SELECT tenant FROM key_window ORDER BY tenant COLLATE "C" DESC, digest DESC LIMIT 1),
                (SELECT digest FROM key_window ORDER BY tenant COLLATE "C" DESC, digest DESC LIMIT 1)
            """);
        AddParameter(command, "maximum_rows", maximumRows, DbType.Int32);
        if (afterTenant is not null)
        {
            AddParameter(command, "after_tenant", afterTenant, DbType.String);
            AddParameter(command, "after_digest", afterDigest, DbType.Binary);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Content collection did not return its page summary.");
        }

        var examined = checked((int)reader.GetInt64(0));
        var deleted = checked((int)reader.GetInt64(1));
        var next = examined == maximumRows
            ? EncodeContentCursor(reader.GetString(2), reader.GetFieldValue<byte[]>(3))
            : null;
        return new DocumentContentCollectionPage(examined, deleted, next);
    }

    private static (string? Tenant, byte[]? Digest) DecodeContentCursor(string? cursor)
    {
        if (cursor is null)
        {
            return (null, null);
        }

        if (cursor.Length is 0 or > 392)
        {
            throw new ArgumentException("Invalid content collection cursor.", nameof(cursor));
        }

        try
        {
            var encoded = Convert.FromBase64String(cursor);
            if (encoded.Length < 35 || !string.Equals(Convert.ToBase64String(encoded), cursor, StringComparison.Ordinal))
            {
                throw new ArgumentException("Invalid content collection cursor.", nameof(cursor));
            }

            var tenantLength = BinaryPrimitives.ReadUInt16BigEndian(encoded);
            if (tenantLength is 0 or > 256 || encoded.Length != 2 + tenantLength + 32)
            {
                throw new ArgumentException("Invalid content collection cursor.", nameof(cursor));
            }

            var tenant = StrictUtf8.GetString(encoded.AsSpan(2, tenantLength));
            DocumentValidation.Key(tenant, nameof(cursor), 256);
            return (tenant, encoded.AsSpan(2 + tenantLength, 32).ToArray());
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new ArgumentException("Invalid content collection cursor.", nameof(cursor), exception);
        }
    }

    private static string EncodeContentCursor(string tenant, byte[] digest)
    {
        var tenantBytes = Encoding.UTF8.GetBytes(tenant);
        var data = new byte[2 + tenantBytes.Length + digest.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data, checked((ushort)tenantBytes.Length));
        tenantBytes.CopyTo(data.AsSpan(2));
        digest.CopyTo(data.AsSpan(2 + tenantBytes.Length));
        return Convert.ToBase64String(data);
    }

    private async ValueTask<long> AdvanceRevisionAsync(DbConnection connection, DbTransaction transaction, string tenant,
        string collection, string id, long expectedRevision, CancellationToken cancellationToken)
    {
        await using (var command = Command(connection, transaction, $"UPDATE {DocumentsTable} SET revision = {RevisionExpression}, updated_at = clock_timestamp() WHERE tenant = @tenant AND collection = @collection AND id = @id AND revision = @expected RETURNING revision"))
        {
            AddParameter(command, "tenant", tenant);
            AddParameter(command, "collection", collection);
            AddParameter(command, "id", id);
            AddParameter(command, "expected", expectedRevision, DbType.Int64);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
            {
                return (long)value;
            }
        }

        await using var current = Command(connection, transaction, $"SELECT revision FROM {DocumentsTable} WHERE tenant = @tenant AND collection = @collection AND id = @id");
        AddParameter(current, "tenant", tenant);
        AddParameter(current, "collection", collection);
        AddParameter(current, "id", id);
        var actual = await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        throw new DocumentConcurrencyException(tenant, collection, id, expectedRevision, actual is null or DBNull ? null : (long)actual);
    }

    private async ValueTask<byte[]?> LockContentAsync(DbConnection connection, DbTransaction transaction, string tenant,
        byte[] digest, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT data FROM {ContentTable} WHERE tenant = @tenant AND digest = @digest FOR KEY SHARE");
        AddParameter(command, "tenant", tenant);
        AddParameter(command, "digest", digest, DbType.Binary);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : (byte[])value;
    }

    private static void ValidateContentKey<T>(string tenant, DocumentCollectionDefinition<T> collection, string id, long? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        DocumentValidation.Key(tenant, nameof(tenant), 256);
        DocumentValidation.Key(id, nameof(id), 512);
        if (expectedRevision is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision.Value);
        }
    }
}

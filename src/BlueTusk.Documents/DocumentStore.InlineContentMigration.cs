using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlueTusk.Documents;

public sealed partial class DocumentStore
{
    /// <summary>
    /// Explicitly moves a bounded page of one old JSON schema version into typed JSON plus attached bytes.
    /// The page commits atomically. Retry the same cursor after a conflict; a null cursor starts another sweep.
    /// </summary>
    public async ValueTask<DocumentMigrationPage> MigrateInlineContentPageAsync<T>(string tenant,
        DocumentCollectionDefinition<T> targetCollection, DocumentInlineContentMigration<T> migration,
        int pageSize = 100, string? afterId = null, CancellationToken cancellationToken = default)
    {
        ValidateContentKey(tenant, targetCollection, afterId ?? "_");
        ArgumentNullException.ThrowIfNull(migration);
        if (migration.ToVersion != targetCollection.SchemaVersion)
        {
            throw new ArgumentException("The migration target version must match the target collection.", nameof(migration));
        }
        if (pageSize <= 0 || pageSize > Math.Min(Options.MaxPageSize, Options.MaxSessionOperations))
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var rows = new List<InlineContentMigrationRow>(pageSize);
        var inputBudget = Options.MaxPageBytes;
        var outputBudget = Math.Min(Options.MaxPageBytes, Options.MaxSessionBytes);
        long inputBytes = 0;
        long stagedBytes = 0;
        var more = false;
        var scanAfter = afterId;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        while (rows.Count < pageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id;
            string raw;
            long revision;
            int rawBytes;
            // Both materialized CTEs contain at most one row. The table's body CHECK caps
            // that row at MaxDocumentBytes, even when its text exceeds the remaining page budget.
            await using (var select = Command(connection, null, $"""
                WITH candidate AS MATERIALIZED (
                    SELECT id, revision, body FROM {DocumentsTable}
                    WHERE tenant=@tenant AND collection=@collection AND schema_version=@version
                      AND (@after IS NULL OR id>@after COLLATE "C")
                    ORDER BY id LIMIT 1),
                encoded AS MATERIALIZED (
                    SELECT id, revision, body::text AS raw FROM candidate)
                SELECT id, revision, octet_length(raw),
                    CASE WHEN octet_length(raw)<=@remaining THEN raw ELSE NULL END
                FROM encoded
                """))
            {
                AddParameter(select, "tenant", tenant);
                AddParameter(select, "collection", targetCollection.Name);
                AddParameter(select, "version", migration.FromVersion, DbType.Int32);
                AddParameter(select, "after", scanAfter, DbType.String);
                AddParameter(select, "remaining", inputBudget - inputBytes, DbType.Int64);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
                id = reader.GetString(0);
                revision = reader.GetInt64(1);
                rawBytes = reader.GetInt32(2);
                if (reader.IsDBNull(3))
                {
                    more = true;
                    break;
                }
                raw = reader.GetString(3);
            }

            inputBytes = checked(inputBytes + rawBytes);
            using var json = JsonDocument.Parse(raw);
            var extracted = migration.Transform(json.RootElement)
                ?? throw new InvalidOperationException("The inline-content transform returned no result.");
            ArgumentNullException.ThrowIfNull(extracted.Value);
            if (extracted.Content.Length > Options.MaxDocumentBytes)
            {
                throw new ArgumentException("Extracted content exceeds the configured document byte budget.", nameof(migration));
            }

            // Own callback memory before another callback or await. The body serializer
            // enforces the same per-document limit as ordinary typed writes.
            var content = extracted.Content.ToArray();
            using var bodyStream = new MigrationBodyStream(Options.MaxDocumentBytes);
            JsonSerializer.Serialize(bodyStream, extracted.Value, targetCollection.JsonTypeInfo);
            var bodyBytes = bodyStream.GetBuffer().AsMemory(0, checked((int)bodyStream.Length));
            using var typedJson = JsonDocument.Parse(bodyBytes);
            if (typedJson.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("The transformed document must serialize to a JSON object.", nameof(migration));
            }
            var nextBytes = checked(stagedBytes + bodyBytes.Length + content.Length);
            if (nextBytes > outputBudget)
            {
                if (rows.Count == 0)
                {
                    throw new InvalidOperationException("A transformed document exceeds the migration page byte budget.");
                }
                more = true;
                break;
            }

            stagedBytes = nextBytes;
            rows.Add(new InlineContentMigrationRow(id, revision,
                Encoding.UTF8.GetString(bodyBytes.Span), content, SHA256.HashData(content)));
            scanAfter = id;
        }

        if (!more && rows.Count == pageSize)
        {
            // Probe only the key after a full page; the next body is never read merely
            // to decide whether the caller needs another page.
            await using var probe = Command(connection, null, $"""
                SELECT EXISTS(SELECT 1 FROM {DocumentsTable}
                    WHERE tenant=@tenant AND collection=@collection AND schema_version=@version
                      AND id>@after COLLATE "C" LIMIT 1)
                """);
            AddParameter(probe, "tenant", tenant);
            AddParameter(probe, "collection", targetCollection.Name);
            AddParameter(probe, "version", migration.FromVersion, DbType.Int32);
            AddParameter(probe, "after", scanAfter);
            more = (bool)(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (more && rows.Count == 0)
        {
            throw new InvalidOperationException("A stored document exceeds the migration page byte budget.");
        }
        if (rows.Count == 0)
        {
            return new DocumentMigrationPage(0, null);
        }

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var linked = Command(connection, transaction, $"SELECT 1 FROM {ContentLinksTable} WHERE tenant=@tenant AND collection=@collection AND id=@id"))
            {
                AddParameter(linked, "tenant", tenant);
                AddParameter(linked, "collection", targetCollection.Name);
                AddParameter(linked, "id", row.Id);
                if (await linked.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                {
                    throw new InvalidOperationException("An inline-content migration cannot replace an existing attachment link.");
                }
            }

            await using (var update = Command(connection, transaction, $"""
                UPDATE {DocumentsTable}
                SET body=CAST(@body AS jsonb), revision={RevisionExpression}, schema_version=@to,
                    updated_at=clock_timestamp()
                WHERE tenant=@tenant AND collection=@collection AND id=@id
                  AND revision=@expected AND schema_version=@from
                RETURNING revision
                """))
            {
                AddParameter(update, "tenant", tenant);
                AddParameter(update, "collection", targetCollection.Name);
                AddParameter(update, "id", row.Id);
                AddParameter(update, "body", row.Body);
                AddParameter(update, "expected", row.Revision, DbType.Int64);
                AddParameter(update, "from", migration.FromVersion, DbType.Int32);
                AddParameter(update, "to", migration.ToVersion, DbType.Int32);
                if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null or DBNull)
                {
                    await using var current = Command(connection, transaction, $"SELECT revision FROM {DocumentsTable} WHERE tenant=@tenant AND collection=@collection AND id=@id");
                    AddParameter(current, "tenant", tenant);
                    AddParameter(current, "collection", targetCollection.Name);
                    AddParameter(current, "id", row.Id);
                    var actual = await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    throw new DocumentConcurrencyException(tenant, targetCollection.Name, row.Id, row.Revision,
                        actual is null or DBNull ? null : (long)actual);
                }
            }

            var secured = false;
            for (var attempt = 0; attempt < 4 && !secured; attempt++)
            {
                var existing = await LockContentAsync(connection, transaction, tenant, row.Digest, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    await using var insert = Command(connection, transaction,
                        $"INSERT INTO {ContentTable} (tenant, digest, data) VALUES (@tenant, @digest, @data) ON CONFLICT (tenant, digest) DO NOTHING");
                    AddParameter(insert, "tenant", tenant);
                    AddParameter(insert, "digest", row.Digest, DbType.Binary);
                    AddParameter(insert, "data", row.Content, DbType.Binary);
                    _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    existing = await LockContentAsync(connection, transaction, tenant, row.Digest, cancellationToken).ConfigureAwait(false);
                }
                if (existing is not null)
                {
                    if (!existing.AsSpan().SequenceEqual(row.Content))
                    {
                        throw new InvalidOperationException("The content digest is already associated with different bytes.");
                    }
                    secured = true;
                }
            }
            if (!secured)
            {
                throw new InvalidOperationException("Content collection repeatedly raced with inline migration; retry the page.");
            }

            await using (var link = Command(connection, transaction,
                $"INSERT INTO {ContentLinksTable} (tenant, collection, id, digest) VALUES (@tenant, @collection, @id, @digest)"))
            {
                AddParameter(link, "tenant", tenant);
                AddParameter(link, "collection", targetCollection.Name);
                AddParameter(link, "id", row.Id);
                AddParameter(link, "digest", row.Digest, DbType.Binary);
                _ = await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentMigrationPage(rows.Count, more ? rows[^1].Id : null);
    }

    private sealed record InlineContentMigrationRow(string Id, long Revision, string Body, byte[] Content, byte[] Digest);

    private sealed class MigrationBodyStream(int maximumBytes) : MemoryStream
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
            if (Length + count > maximumBytes)
            {
                throw new ArgumentException("Serialized document exceeds the document byte budget.");
            }
        }
    }
}

using System.Data.Common;

namespace BlueTusk.Projections;

public sealed partial class PostgreSqlProjectionStore
{
    public async ValueTask<ProjectionPublication> ReadPublicationAsync(string projectionName, CancellationToken cancellationToken = default)
    {
        ProjectionValidation.Key(projectionName, nameof(projectionName));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT active_version, publication_revision FROM {_schema}.heads WHERE projection = @projection", ("projection", projectionName));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Publication(reader) : new ProjectionPublication(null, 0);
    }

    /// <summary>Reads one tenant's published documents in ordinal key order. Continuations fail if publication changed.</summary>
    public async ValueTask<ProjectionDocumentPage> ReadActivePageAsync(string projectionName, string tenantId,
        int maximumDocuments = 256, int maximumPayloadBytes = 8_388_608, ProjectionPageCursor? after = null,
        string? afterKey = null, string? throughKey = null, CancellationToken cancellationToken = default)
    {
        ProjectionValidation.Key(projectionName, nameof(projectionName));
        ProjectionValidation.Key(tenantId, nameof(tenantId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDocuments);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumDocuments, _options.MaximumDependencyPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumPayloadBytes, _options.MaximumWriteBatchBytes);
        if (afterKey is not null) { ProjectionValidation.Key(afterKey, nameof(afterKey)); }
        if (throughKey is not null) { ProjectionValidation.Key(throughKey, nameof(throughKey)); }
        if (after is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(after.Version);
            ArgumentOutOfRangeException.ThrowIfNegative(after.Revision);
            ProjectionValidation.Key(after.AfterKey, nameof(after));
            if (afterKey is not null) { throw new ArgumentException("Supply either a continuation or an initial lower key bound.", nameof(after)); }
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // The payload CASE prevents an over-budget document (including the lookahead) from crossing
        // the wire. All metadata and rows belong to a single PostgreSQL statement snapshot.
        await using var command = Command(connection, null, $"""
            WITH h AS (SELECT active_version, publication_revision FROM {_schema}.heads WHERE projection = @projection),
            page AS (
                SELECT d.document_key, d.payload, row_number() OVER (ORDER BY d.document_key COLLATE "C") AS ordinal,
                    sum(octet_length(d.payload)::bigint) OVER (ORDER BY d.document_key COLLATE "C") AS bytes
                FROM (SELECT document_key, payload FROM {_schema}.documents
                    WHERE projection = @projection AND version = (SELECT active_version FROM h) AND tenant_id = @tenant
                        AND document_key > @after COLLATE "C" AND (@has_through = false OR document_key <= @through COLLATE "C")
                    ORDER BY document_key COLLATE "C" LIMIT @limit) d)
            SELECT h.active_version, h.publication_revision, p.document_key,
                CASE WHEN p.ordinal <= @count AND p.bytes <= @bytes THEN p.payload ELSE NULL END,
                p.ordinal, p.bytes FROM h LEFT JOIN page p ON true ORDER BY p.document_key COLLATE "C"
            """, ("projection", projectionName), ("tenant", tenantId), ("after", after?.AfterKey ?? afterKey ?? string.Empty),
            ("has_through", throughKey is not null), ("through", throughKey ?? string.Empty),
            ("limit", maximumDocuments + 1), ("count", maximumDocuments), ("bytes", maximumPayloadBytes));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var publication = new ProjectionPublication(null, 0);
        var documents = new List<ProjectionDocument>();
        var more = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            publication = Publication(reader);
            if (after is not null && (publication.Version != after.Version || publication.Revision != after.Revision))
            {
                throw new InvalidOperationException("The published projection changed during pagination. Restart from its first page.");
            }

            if (reader.IsDBNull(2)) { continue; }
            if (reader.IsDBNull(3)) { more = true; continue; }
            documents.Add(new ProjectionDocument(tenantId, reader.GetString(2), reader.GetFieldValue<byte[]>(3)));
        }

        if (more && documents.Count == 0)
        {
            throw new ProjectionBoundExceededException("The first published document exceeds the page byte bound.");
        }

        return new ProjectionDocumentPage(publication, documents.AsReadOnly(), more
            ? new ProjectionPageCursor(publication.Version!.Value, publication.Revision, documents[^1].Key) : null);
    }

    public async ValueTask<ProjectionPublishedAggregate> ReadActiveAggregateAsync(string projectionName, string tenantId,
        string groupKey, string metric, CancellationToken cancellationToken = default)
    {
        ProjectionValidation.Key(projectionName, nameof(projectionName));
        ProjectionValidation.Key(tenantId, nameof(tenantId));
        ProjectionValidation.Key(groupKey, nameof(groupKey));
        ProjectionValidation.Key(metric, nameof(metric));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            SELECT h.active_version, h.publication_revision, COALESCE(a.value, 0)
            FROM {_schema}.heads h LEFT JOIN {_schema}.aggregates a
                ON a.projection = h.projection AND a.version = h.active_version AND a.tenant_id = @tenant
                AND a.group_key = @group_key AND a.metric = @metric WHERE h.projection = @projection
            """, ("projection", projectionName), ("tenant", tenantId), ("group_key", groupKey), ("metric", metric));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProjectionPublishedAggregate(Publication(reader), reader.GetDecimal(2))
            : new ProjectionPublishedAggregate(new ProjectionPublication(null, 0), 0);
    }

    private static ProjectionPublication Publication(DbDataReader reader) =>
        new(reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.GetInt64(1));
}

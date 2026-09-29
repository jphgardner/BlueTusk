using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;

namespace BlueTusk.Search;

public sealed partial class PostgreSqlSearchStore
{
    /// <summary>Materializes a bounded rank snapshot. Each page rechecks current source versions and permissions.</summary>
    public async ValueTask<SearchPage> SearchAsync(SearchScope scope, SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var vector = request.Vector;
        if (request.Mode is not SearchMode.FullText)
        {
            if (_vectors is null)
            {
                throw new InvalidOperationException("Vector or hybrid retrieval requires a configured vector adapter.");
            }

            if (vector.IsEmpty)
            {
                ThrowIfDisposed();
                if (!await _ingestionSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    throw new SearchBackpressureException();
                }

                try
                {
                    ThrowIfDisposed();
                    vector = (await EmbedAsync(scope, [request.Text], cancellationToken).ConfigureAwait(false))[0];
                }
                finally
                {
                    _ingestionSlots.Release();
                }
            }

            _ = _vectors.Encode(vector.Span);
        }

        var rankingSlotTaken = false;
        if (_ranking is not null)
        {
            if (!await _rankingSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new SearchBackpressureException();
            }

            rankingSlotTaken = true;
        }

        try
        {
            var queryId = Guid.NewGuid();
            var reserved = false;
            try
            {
                // Admission counts both pending and published snapshots, but releases its scope lock
                // before any candidate work or caller-provided ranking code begins.
                await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
                await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                {
                    await AdmitQueryAsync(connection, transaction, scope, request.CandidateLimit, cancellationToken).ConfigureAwait(false);
                    await using var command = Command(connection, transaction, $"INSERT INTO {_schema}.queries (query_id,tenant,index_name,scope_fingerprint,expires_at,ready,reserved_rows,reserved_bytes) VALUES (@query,@tenant,@index,@scope,clock_timestamp()+(@lifetime * interval '1 second'),false,@rows,@bytes)");
                    ScopeParameters(command, scope);
                    Parameter(command, "query", queryId, DbType.Guid);
                    Parameter(command, "scope", scope.Fingerprint);
                    Parameter(command, "lifetime", Options.QueryLifetime.TotalSeconds, DbType.Double);
                    Parameter(command, "rows", request.CandidateLimit, DbType.Int64);
                    Parameter(command, "bytes", (long)request.CandidateLimit * 552, DbType.Int64);
                    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                reserved = true;
                await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
                await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                {
                    await using var command = Command(connection, transaction, CandidateSql(request));
                    ScopeParameters(command, scope);
                    Parameter(command, "query", queryId, DbType.Guid);
                    Parameter(command, "candidates", request.CandidateLimit, DbType.Int32);
                    if (request.Mode is not SearchMode.Vector)
                    {
                        Parameter(command, "text", request.Text);
                    }

                    if (request.Mode is not SearchMode.FullText)
                    {
                        Parameter(command, "vector", _vectors!.Encode(vector.Span));
                    }

                    if (request.Mode is SearchMode.Hybrid)
                    {
                        Parameter(command, "text_weight", request.FullTextWeight, DbType.Double);
                        Parameter(command, "vector_weight", request.VectorWeight, DbType.Double);
                        Parameter(command, "rrf", request.ReciprocalRankConstant, DbType.Int32);
                    }

                    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    if (_ranking is null)
                    {
                        await PublishQueryAsync(connection, transaction, queryId, cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                if (_ranking is not null)
                {
                    var candidates = await ReadRankingCandidatesAsync(queryId, scope, cancellationToken).ConfigureAwait(false);
                    var scores = await _ranking.RankAsync(scope, request.Text, candidates.AsReadOnly(), cancellationToken).ConfigureAwait(false);

                    await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
                    await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await PublishQueryAsync(connection, transaction, queryId, cancellationToken).ConfigureAwait(false);
                        await ApplyRankingAsync(connection, transaction, queryId, candidates, scores, cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                if (reserved)
                {
                    await TryRemovePendingQueryAsync(queryId).ConfigureAwait(false);
                }

                throw;
            }

            // The first page is ordinary retrieval; it should not occupy ranker capacity.
            if (rankingSlotTaken)
            {
                _rankingSlots.Release();
                rankingSlotTaken = false;
            }

            return await ContinueSearchAsync(scope, new SearchCursor(queryId, 0), request.PageSize, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (rankingSlotTaken)
            {
                _rankingSlots.Release();
            }
        }
    }

    public async ValueTask<SearchPage> ContinueSearchAsync(SearchScope scope, SearchCursor cursor, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor.AfterRank);
        if (pageSize < 1 || pageSize > Options.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        DateTimeOffset expires;
        await using (var command = Command(connection, transaction, $"SELECT expires_at FROM {_schema}.queries WHERE query_id=@query AND tenant=@tenant AND index_name=@index AND scope_fingerprint=@scope AND ready AND expires_at>clock_timestamp()"))
        {
            ScopeParameters(command, scope, includePrincipals: false);
            Parameter(command, "query", cursor.QueryId, DbType.Guid);
            Parameter(command, "scope", scope.Fingerprint);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            expires = value switch
            {
                DateTimeOffset timestamp => timestamp,
                DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
                _ => throw new SearchCursorExpiredException(),
            };
        }

        var hits = new List<SearchHit>(pageSize);
        var lastRank = cursor.AfterRank;
        var more = false;
        await using (var command = Command(connection, transaction, $"""
            WITH candidates AS (
                SELECT r.rank,c.document_id,c.ordinal,c.source_version,d.title,c.content,d.metadata,r.score
                FROM {_schema}.query_results r
                JOIN {_schema}.chunks c ON c.tenant=@tenant AND c.index_name=@index
                    AND c.document_id=r.document_id AND c.ordinal=r.ordinal AND c.source_version=r.source_version
                JOIN {_schema}.documents d ON d.tenant=c.tenant AND d.index_name=c.index_name
                    AND d.document_id=c.document_id AND d.source_version=c.source_version AND NOT d.deleted
                WHERE r.query_id=@query AND r.rank>@after AND {PermissionSql("d")}
                ORDER BY r.rank LIMIT @count),
            bounded AS (
                SELECT *,sum(octet_length(content)+octet_length(title)+octet_length(metadata::text)) OVER (ORDER BY rank) AS total_bytes
                FROM candidates)
            SELECT rank,document_id,ordinal,source_version,
                CASE WHEN total_bytes<=@bytes THEN title ELSE NULL END,
                CASE WHEN total_bytes<=@bytes THEN content ELSE NULL END,
                CASE WHEN total_bytes<=@bytes THEN metadata::text ELSE NULL END,score
            FROM bounded ORDER BY rank
            """))
        {
            ScopeParameters(command, scope);
            Parameter(command, "query", cursor.QueryId, DbType.Guid);
            Parameter(command, "after", cursor.AfterRank, DbType.Int32);
            Parameter(command, "count", pageSize + 1, DbType.Int32);
            Parameter(command, "bytes", Options.MaxPageBytes, DbType.Int64);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (hits.Count == pageSize || reader.IsDBNull(4))
                {
                    more = true;
                    break;
                }

                lastRank = reader.GetInt32(0);
                using var metadata = JsonDocument.Parse(reader.GetString(6));
                hits.Add(new SearchHit(reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5), metadata.RootElement.Clone(), reader.GetDouble(7)));
            }
        }

        if (more && hits.Count == 0)
        {
            throw new SearchBackpressureException();
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SearchPage(hits.AsReadOnly(), more ? new SearchCursor(cursor.QueryId, lastRank) : null, expires);
    }

    private string CandidateSql(SearchRequest request)
    {
        var eligible = $"""
            eligible AS NOT MATERIALIZED (
                SELECT c.document_id,c.ordinal,c.source_version,c.terms{(_vectors is null ? string.Empty : ",c.embedding")}
                FROM {_schema}.chunks c JOIN {_schema}.documents d
                  ON d.tenant=c.tenant AND d.index_name=c.index_name AND d.document_id=c.document_id
                    AND d.source_version=c.source_version AND NOT d.deleted
                WHERE c.tenant=@tenant AND c.index_name=@index AND {PermissionSql("d")})
            """;
        var text = """
            text_candidates AS (
                SELECT document_id,ordinal,source_version,
                    ts_rank_cd(terms,websearch_to_tsquery('pg_catalog.simple'::regconfig,@text),32)::double precision AS score
                FROM eligible WHERE terms @@ websearch_to_tsquery('pg_catalog.simple'::regconfig,@text)
                ORDER BY score DESC,document_id,ordinal LIMIT @candidates),
            text_ranked AS (
                SELECT *,row_number() OVER (ORDER BY score DESC,document_id,ordinal) AS position FROM text_candidates)
            """;
        var vector = string.Empty;
        if (request.Mode is not SearchMode.FullText)
        {
            var distance = _vectors!.DistanceSql("embedding", "@vector");
            // Adding zero prevents an ANN index order scan when exact retrieval is requested.
            if (!request.ApproximateVectorSearch)
            {
                distance = "(" + distance + ") + 0.0";
            }

            vector = $"""
                vector_candidates AS (
                    SELECT document_id,ordinal,source_version,({distance}) AS distance
                    FROM eligible WHERE embedding IS NOT NULL
                    ORDER BY distance,document_id,ordinal LIMIT @candidates),
                vector_ranked AS (
                    SELECT *,row_number() OVER (ORDER BY distance,document_id,ordinal) AS position FROM vector_candidates)
                """;
        }

        var candidates = request.Mode switch
        {
            SearchMode.FullText => text + ", ranked AS (SELECT document_id,ordinal,source_version,score FROM text_ranked)",
            SearchMode.Vector => vector + ", ranked AS (SELECT document_id,ordinal,source_version,1.0-distance AS score FROM vector_ranked)",
            _ => text + "," + vector + """
                ,combined AS (
                    SELECT document_id,ordinal,source_version,@text_weight/(@rrf+position) AS score FROM text_ranked
                    UNION ALL
                    SELECT document_id,ordinal,source_version,@vector_weight/(@rrf+position) AS score FROM vector_ranked),
                ranked AS (
                    SELECT document_id,ordinal,source_version,sum(score) AS score FROM combined
                    GROUP BY document_id,ordinal,source_version)
                """,
        };
        return $"""
            WITH {eligible},{candidates},
            final AS (
                SELECT document_id,ordinal,source_version,score,
                    row_number() OVER (ORDER BY score DESC,document_id,ordinal) AS rank
                FROM ranked ORDER BY score DESC,document_id,ordinal LIMIT @candidates)
            INSERT INTO {_schema}.query_results (query_id,rank,document_id,ordinal,source_version,score)
            SELECT @query,rank::integer,document_id,ordinal,source_version,score FROM final
            """;
    }

    private async ValueTask<List<SearchHit>> ReadRankingCandidatesAsync(Guid queryId, SearchScope scope, CancellationToken cancellationToken)
    {
        var candidates = new List<SearchHit>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = Command(connection, null, $"""
            WITH candidates AS (
                SELECT r.rank,c.document_id,c.ordinal,c.source_version,d.title,c.content,d.metadata,r.score,
                    sum(octet_length(c.content)+octet_length(d.title)+octet_length(d.metadata::text)) OVER (ORDER BY r.rank) AS total_bytes
                FROM {_schema}.query_results r
                JOIN {_schema}.chunks c ON c.tenant=@tenant AND c.index_name=@index
                    AND c.document_id=r.document_id AND c.ordinal=r.ordinal AND c.source_version=r.source_version
                JOIN {_schema}.documents d ON d.tenant=c.tenant AND d.index_name=c.index_name
                    AND d.document_id=c.document_id AND d.source_version=c.source_version AND NOT d.deleted
                WHERE r.query_id=@query AND {PermissionSql("d")})
            SELECT document_id,ordinal,source_version,
                CASE WHEN total_bytes<=@bytes THEN title ELSE NULL END,
                CASE WHEN total_bytes<=@bytes THEN content ELSE NULL END,
                CASE WHEN total_bytes<=@bytes THEN metadata::text ELSE NULL END,score
            FROM candidates ORDER BY rank
            """))
        {
            ScopeParameters(command, scope);
            Parameter(command, "query", queryId, DbType.Guid);
            Parameter(command, "bytes", Options.MaxRerankingBytes, DbType.Int64);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(3))
                {
                    throw new SearchBackpressureException();
                }

                using var metadata = JsonDocument.Parse(reader.GetString(5));
                candidates.Add(new SearchHit(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), metadata.RootElement.Clone(), reader.GetDouble(6)));
            }
        }

        return candidates;
    }

    private async ValueTask ApplyRankingAsync(DbConnection connection, DbTransaction transaction, Guid queryId, List<SearchHit> candidates,
        IReadOnlyList<SearchRankingScore> scores, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scores);
        if (scores.Count > candidates.Count)
        {
            throw new InvalidOperationException("The ranking extension returned more results than the bounded candidate set.");
        }

        var byKey = candidates.ToDictionary(static x => (x.DocumentId, x.ChunkOrdinal));
        var seen = new HashSet<(string, int)>();
        foreach (var score in scores)
        {
            if (!double.IsFinite(score.Score) || !byKey.ContainsKey((score.DocumentId, score.ChunkOrdinal)) || !seen.Add((score.DocumentId, score.ChunkOrdinal)))
            {
                throw new InvalidOperationException("The ranking extension returned a non-finite score, duplicate, or candidate outside the authorized input set.");
            }
        }

        await using (var delete = Command(connection, transaction, $"DELETE FROM {_schema}.query_results WHERE query_id=@query"))
        {
            Parameter(delete, "query", queryId, DbType.Guid);
            _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var ordered = scores.OrderByDescending(static x => x.Score).ThenBy(static x => x.DocumentId, StringComparer.Ordinal).ThenBy(static x => x.ChunkOrdinal).ToArray();
        var rank = 0;
        foreach (var chunk in ordered.Chunk(128))
        {
            await using var command = Command(connection, transaction, string.Empty);
            var sql = new System.Text.StringBuilder($"INSERT INTO {_schema}.query_results (query_id,rank,document_id,ordinal,source_version,score) VALUES ");
            Parameter(command, "query", queryId, DbType.Guid);
            for (var i = 0; i < chunk.Length; i++)
            {
                if (i != 0)
                {
                    sql.Append(',');
                }

                sql.Append(System.Globalization.CultureInfo.InvariantCulture, $"(@query,@rank{i},@id{i},@ordinal{i},@version{i},@score{i})");
                var hit = byKey[(chunk[i].DocumentId, chunk[i].ChunkOrdinal)];
                Parameter(command, $"rank{i}", ++rank, DbType.Int32);
                Parameter(command, $"id{i}", hit.DocumentId);
                Parameter(command, $"ordinal{i}", hit.ChunkOrdinal, DbType.Int32);
                Parameter(command, $"version{i}", hit.SourceVersion, DbType.Int64);
                Parameter(command, $"score{i}", chunk[i].Score, DbType.Double);
            }

            command.CommandText = sql.ToString();
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask PublishQueryAsync(DbConnection connection, DbTransaction transaction, Guid queryId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            $"UPDATE {_schema}.queries SET ready=true,expires_at=clock_timestamp()+(@lifetime * interval '1 second') WHERE query_id=@query AND NOT ready AND expires_at>clock_timestamp()");
        Parameter(command, "query", queryId, DbType.Guid);
        Parameter(command, "lifetime", Options.QueryLifetime.TotalSeconds, DbType.Double);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new SearchCursorExpiredException();
        }
    }

    private async ValueTask TryRemovePendingQueryAsync(Guid queryId)
    {
        // Cleanup uses an independent, short deadline so caller cancellation cannot strand a
        // reservation indefinitely. Expiry pruning remains the recovery path if the DB is down.
        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var connection = await OpenAsync(cleanupDeadline.Token).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cleanupDeadline.Token).ConfigureAwait(false);
            await LockSnapshotBudgetAsync(connection, transaction, cleanupDeadline.Token).ConfigureAwait(false);
            long? reservedRows = null;
            long reservedBytes = 0;
            await using (var command = Command(connection, transaction, $"DELETE FROM {_schema}.queries WHERE query_id=@query AND NOT ready RETURNING reserved_rows,reserved_bytes"))
            {
                Parameter(command, "query", queryId, DbType.Guid);
                await using var reader = await command.ExecuteReaderAsync(cleanupDeadline.Token).ConfigureAwait(false);
                if (await reader.ReadAsync(cleanupDeadline.Token).ConfigureAwait(false))
                {
                    reservedRows = reader.GetInt64(0);
                    reservedBytes = reader.GetInt64(1);
                }
            }
            if (reservedRows is not null)
                await ReleaseSnapshotBudgetAsync(connection, transaction, 1, reservedRows.Value, reservedBytes, cleanupDeadline.Token).ConfigureAwait(false);
            await transaction.CommitAsync(cleanupDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original query failure is more useful to callers. The pending row is bounded
            // by its database-clock deadline and can be pruned on admission or by the host.
        }
    }

    private async ValueTask AdmitQueryAsync(DbConnection connection, DbTransaction transaction, SearchScope scope, int candidateLimit, CancellationToken cancellationToken)
    {
        await LockSnapshotBudgetAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using (var guard = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@name, 0))"))
        {
            Parameter(guard, "name", "BlueTusk.Search.Query:" + SearchValidation.Hash(scope.Tenant + "\0" + scope.Index));
            _ = await guard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _ = await PruneQueriesAsync(connection, transaction, scope, 8, cancellationToken).ConfigureAwait(false);
        _ = await PruneQueriesAsync(connection, transaction, null, 8, cancellationToken).ConfigureAwait(false);

        await using (var count = Command(connection, transaction, $"SELECT count(*) FROM (SELECT 1 FROM {_schema}.queries WHERE tenant=@tenant AND index_name=@index LIMIT @maximum) bounded"))
        {
            ScopeParameters(count, scope, includePrincipals: false);
            Parameter(count, "maximum", Options.MaxActiveQueriesPerScope, DbType.Int32);
            var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (total >= Options.MaxActiveQueriesPerScope)
            {
                throw new SearchBackpressureException();
            }
        }

        await using (var reserve = Command(connection, transaction, $"""
            UPDATE {_schema}.snapshot_budget SET reserved_queries=reserved_queries+1,
                reserved_rows=reserved_rows+@rows,reserved_bytes=reserved_bytes+@bytes
            WHERE singleton AND max_queries=@max_queries AND max_rows=@max_rows AND max_bytes=@max_bytes
                AND reserved_queries<max_queries AND reserved_rows<=max_rows-@rows AND reserved_bytes<=max_bytes-@bytes
            """))
        {
            Parameter(reserve, "rows", candidateLimit, DbType.Int64);
            Parameter(reserve, "bytes", (long)candidateLimit * 552, DbType.Int64);
            Parameter(reserve, "max_queries", Options.MaxRetainedQueries, DbType.Int64);
            Parameter(reserve, "max_rows", Options.MaxRetainedRankRows, DbType.Int64);
            Parameter(reserve, "max_bytes", Options.MaxRetainedRankBytes, DbType.Int64);
            if (await reserve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new SearchBackpressureException();
        }
    }

    private async ValueTask LockSnapshotBudgetAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        // A simultaneous read burst should serialize short budget reservations,
        // while a stuck owner must not create an unbounded admission queue.
        var wait = Stopwatch.StartNew();
        while (true)
        {
            await using var command = Command(connection, transaction, "SELECT pg_try_advisory_xact_lock(hashtextextended(@name, 0))");
            Parameter(command, "name", "BlueTusk.Search.SnapshotBudget:" + Options.Schema);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true) { return; }
            if (wait.Elapsed >= TimeSpan.FromMilliseconds(200)) { throw new SearchBackpressureException(); }
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<int> PruneQueriesAsync(DbConnection connection, DbTransaction transaction, SearchScope? scope, int maximum, CancellationToken cancellationToken)
    {
        var filter = scope is null ? string.Empty : "tenant=@tenant AND index_name=@index AND ";
        await using var command = Command(connection, transaction, $"""
            WITH expired AS (
                SELECT query_id FROM {_schema}.queries WHERE {filter}expires_at<=clock_timestamp()
                ORDER BY expires_at LIMIT @maximum FOR UPDATE SKIP LOCKED),
            removed AS (DELETE FROM {_schema}.queries q USING expired e WHERE q.query_id=e.query_id
                RETURNING q.reserved_rows,q.reserved_bytes)
            SELECT count(*),coalesce(sum(reserved_rows),0)::bigint,coalesce(sum(reserved_bytes),0)::bigint FROM removed
            """);
        if (scope is not null)
            ScopeParameters(command, scope, includePrincipals: false);
        Parameter(command, "maximum", maximum, DbType.Int32);
        int removed;
        long rows;
        long bytes;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            removed = checked((int)reader.GetInt64(0));
            rows = reader.GetInt64(1);
            bytes = reader.GetInt64(2);
        }
        if (removed > 0)
            await ReleaseSnapshotBudgetAsync(connection, transaction, removed, rows, bytes, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    private async ValueTask ReleaseSnapshotBudgetAsync(DbConnection connection, DbTransaction transaction, int queries, long rows, long bytes, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            UPDATE {_schema}.snapshot_budget SET reserved_queries=reserved_queries-@queries,
                reserved_rows=reserved_rows-@rows,reserved_bytes=reserved_bytes-@bytes
            WHERE singleton AND reserved_queries>=@queries AND reserved_rows>=@rows AND reserved_bytes>=@bytes
            """);
        Parameter(command, "queries", queries, DbType.Int64);
        Parameter(command, "rows", rows, DbType.Int64);
        Parameter(command, "bytes", bytes, DbType.Int64);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Search snapshot budget accounting differs from retained queries.");
    }

    private void ValidateRequest(SearchRequest request)
    {
        if (!Enum.IsDefined(request.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Text);
        if (request.Text.Contains('\0', StringComparison.Ordinal) || System.Text.Encoding.UTF8.GetByteCount(request.Text) > 16_384 ||
            request.Mode is not SearchMode.Vector && string.IsNullOrWhiteSpace(request.Text) ||
            request.PageSize < 1 || request.PageSize > Options.MaxPageSize ||
            request.CandidateLimit < request.PageSize || request.CandidateLimit > Options.MaxCandidateCount ||
            !double.IsFinite(request.FullTextWeight) || request.FullTextWeight <= 0 || request.FullTextWeight > 1000 ||
            !double.IsFinite(request.VectorWeight) || request.VectorWeight <= 0 || request.VectorWeight > 1000 ||
            request.ReciprocalRankConstant is < 1 or > 10_000)
        {
            throw new ArgumentException("Search request exceeds query, candidate, page or ranking limits.", nameof(request));
        }
    }

    private static string PermissionSql(string alias) =>
        $"({alias}.is_public OR {alias}.principals ?| ARRAY(SELECT jsonb_array_elements_text(CAST(@principals AS jsonb))))";

    private static void ScopeParameters(DbCommand command, SearchScope scope, bool includePrincipals = true)
    {
        Parameter(command, "tenant", scope.Tenant);
        Parameter(command, "index", scope.Index);
        if (includePrincipals)
        {
            Parameter(command, "principals", scope.PrincipalsJson);
        }
    }
}

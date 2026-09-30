using System.Text.Json;

namespace BlueTusk.Search.OpenSearch;

public sealed partial class OpenSearchStore
{
    /// <summary>Persists the bounded rank snapshot before its first page. PostgreSQL owns retention and the cursor survives host restart.</summary>
    public async ValueTask<SearchPage> SearchDurableAsync(SearchScope scope, SearchRequest request, PostgreSqlOpenSearchCursorStore cursors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursors);
        var generation = _indexGeneration;
        var session = await SearchAsync(scope, request, cancellationToken).ConfigureAwait(false);
        var target = await DurableTargetAsync(cancellationToken).ConfigureAwait(false);
        if (generation != _indexGeneration) { throw new SearchCursorExpiredException(); }
        var cursor = await cursors.SaveAsync(target, scope, ScopeKey(scope), session.Hits, cancellationToken).ConfigureAwait(false);
        return await ContinueDurableSearchAsync(scope, cursor, cursors, request.PageSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rechecks physical index generation, exact permission scope and database-clock deadline, then current document ACL/version/deletion.</summary>
    public async ValueTask<SearchPage> ContinueDurableSearchAsync(SearchScope scope, SearchCursor cursor, PostgreSqlOpenSearchCursorStore cursors,
        int pageSize = 20, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(cursor); ArgumentNullException.ThrowIfNull(cursors);
        var target = await DurableTargetAsync(cancellationToken).ConfigureAwait(false);
        var persisted = await cursors.LoadAsync(target, scope, ScopeKey(scope), cursor, cancellationToken).ConfigureAwait(false);
        var session = new OpenSearchSearchSession(_owner, ScopeKey(scope), persisted.Hits, persisted.Expires, databaseDeadline: true);
        var page = await ReadPageAsync(scope, session, pageSize, cursor.AfterRank, cancellationToken).ConfigureAwait(false);
        // Recreating a physical index during the page read must not validate old source-version fences against unrelated new documents.
        if (await DurableTargetAsync(cancellationToken).ConfigureAwait(false) != target) { throw new SearchCursorExpiredException(); }
        return new(page.Hits, page.NextAfterRank is int rank ? new SearchCursor(cursor.QueryId, rank) : null, persisted.Expires);
    }

    private async ValueTask<string> DurableTargetAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = await ReadIndexGenerationAsync(cancellationToken).ConfigureAwait(false);
            if (generation != _indexGeneration) { throw new SearchCursorExpiredException(); }
            return Hash(Options.Endpoint.AbsoluteUri + '\0' + Options.IndexName + '\0' + _contract + '\0' + generation);
        }
        finally { _operations.Release(); }
    }
    private async ValueTask<string> ReadIndexGenerationAsync(CancellationToken cancellationToken)
    {
        using var response = await RequiredAsync(HttpMethod.Get, Options.IndexName + "/_settings?flat_settings=true", null, cancellationToken).ConfigureAwait(false);
        var generation = response.RootElement.GetProperty(Options.IndexName).GetProperty("settings").GetProperty("index.uuid").GetString();
        if (string.IsNullOrWhiteSpace(generation) || generation.Length > 256) { throw new JsonException("OpenSearch physical index generation is missing."); }
        return generation;
    }
}

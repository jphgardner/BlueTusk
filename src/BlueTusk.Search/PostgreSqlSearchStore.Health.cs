using System.Data;

namespace BlueTusk.Search;

public sealed class SearchStoreHealthSnapshot
{
    internal SearchStoreHealthSnapshot(DateTimeOffset databaseTime, int storageVersion, bool hasDocuments, int retained, int active, bool capacityReached)
    { DatabaseTime = databaseTime; StorageVersion = storageVersion; HasDocuments = hasDocuments; ObservedRetainedQueries = retained; ObservedActiveQueries = active; QueryCapacityReached = capacityReached; }
    public DateTimeOffset DatabaseTime { get; }
    public int StorageVersion { get; }
    public bool HasDocuments { get; }
    public int ObservedRetainedQueries { get; }
    public int ObservedActiveQueries { get; }
    public bool QueryCapacityReached { get; }
}

public sealed partial class PostgreSqlSearchStore
{
    /// <summary>Reads the immutable format contract and an indexed bounded scope seam; it does not fetch text, ACLs, vectors or query payloads.</summary>
    public async ValueTask<SearchStoreHealthSnapshot> ReadHealthAsync(SearchScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            WITH bounded_queries AS MATERIALIZED(
              SELECT expires_at FROM {_schema}.queries WHERE tenant=@tenant AND index_name=@index ORDER BY expires_at DESC LIMIT @maximum)
            SELECT clock_timestamp(),storage_version,contract,
              EXISTS(SELECT 1 FROM {_schema}.documents WHERE tenant=@tenant AND index_name=@index),
              (SELECT count(*) FROM bounded_queries),(SELECT count(*) FROM bounded_queries WHERE expires_at>clock_timestamp())
            FROM {_schema}.storage_metadata WHERE singleton
            """);
        Parameter(command, "tenant", scope.Tenant); Parameter(command, "index", scope.Index);
        Parameter(command, "maximum", Options.MaxActiveQueriesPerScope + 1, DbType.Int32);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(1) != CurrentStorageVersion || reader.GetString(2) != _contract)
        { throw new InvalidOperationException("Installed Search storage, chunking, model or vector contract differs."); }
        var retained = checked((int)reader.GetInt64(4)); var active = checked((int)reader.GetInt64(5));
        return new(reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt32(1), reader.GetBoolean(3), retained, active, retained >= Options.MaxActiveQueriesPerScope);
    }
}

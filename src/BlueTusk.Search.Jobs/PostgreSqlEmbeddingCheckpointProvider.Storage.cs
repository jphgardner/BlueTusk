using System.Data.Common;
using System.Globalization;

namespace BlueTusk.Search.Jobs;

public sealed partial class PostgreSqlEmbeddingCheckpointProvider
{
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken, requireInitialized: false).ConfigureAwait(false);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var guard = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@name,0))"))
            { Parameter(guard, "name", "BlueTusk.Search.Embeddings:" + Options.Schema); _ = await guard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
            await using (var create = Command(connection, transaction, $"""
                CREATE SCHEMA IF NOT EXISTS {_schema};
                CREATE TABLE IF NOT EXISTS {_schema}.metadata(singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),version integer NOT NULL,max_models integer NOT NULL,max_records integer NOT NULL,max_bytes bigint NOT NULL,max_batches integer NOT NULL,max_tenant_batches integer NOT NULL,records bigint NOT NULL DEFAULT 0,reserved_bytes bigint NOT NULL DEFAULT 0,CHECK(records>=0 AND reserved_bytes>=0));
                INSERT INTO {_schema}.metadata(version,max_models,max_records,max_bytes,max_batches,max_tenant_batches) VALUES(1,{Options.MaxModels.ToString(CultureInfo.InvariantCulture)},{Options.MaxRecords.ToString(CultureInfo.InvariantCulture)},{Options.MaxReservedVectorBytes.ToString(CultureInfo.InvariantCulture)},{Options.MaxActiveProviderBatches.ToString(CultureInfo.InvariantCulture)},{Options.MaxActiveProviderBatchesPerTenant.ToString(CultureInfo.InvariantCulture)}) ON CONFLICT DO NOTHING;
                CREATE TABLE IF NOT EXISTS {_schema}.models(model text PRIMARY KEY,dimensions integer NOT NULL CHECK(dimensions BETWEEN 1 AND 4096));
                CREATE TABLE IF NOT EXISTS {_schema}.checkpoints(tenant text NOT NULL,index_name text NOT NULL,model text NOT NULL REFERENCES {_schema}.models,text_hash text NOT NULL,dimensions integer NOT NULL,fence bigint NOT NULL CHECK(fence>0),owner uuid NULL,lease_expires timestamptz NULL,expires_at timestamptz NULL,payload bytea NULL,PRIMARY KEY(tenant,index_name,model,text_hash),CHECK(payload IS NULL OR octet_length(payload)=dimensions*4),CHECK((payload IS NULL AND owner IS NOT NULL AND lease_expires IS NOT NULL AND expires_at IS NULL) OR(payload IS NOT NULL AND owner IS NULL AND lease_expires IS NULL AND expires_at IS NOT NULL)));
                CREATE INDEX IF NOT EXISTS checkpoint_expiration ON {_schema}.checkpoints((coalesce(expires_at,lease_expires)));
                CREATE TABLE IF NOT EXISTS {_schema}.leases(owner uuid PRIMARY KEY,tenant text NOT NULL,expires_at timestamptz NOT NULL);
                CREATE INDEX IF NOT EXISTS lease_expiration ON {_schema}.leases(expires_at,tenant);
                """)) { _ = await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
            await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await using (var validate = Command(connection, transaction, $"SELECT version,max_models,max_records,max_bytes,max_batches,max_tenant_batches FROM {_schema}.metadata WHERE singleton"))
            await using (var reader = await validate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (reader.GetInt32(0) != 1 || reader.GetInt32(1) != Options.MaxModels || reader.GetInt32(2) != Options.MaxRecords || reader.GetInt64(3) != Options.MaxReservedVectorBytes ||
                    reader.GetInt32(4) != Options.MaxActiveProviderBatches || reader.GetInt32(5) != Options.MaxActiveProviderBatchesPerTenant)
                { throw new InvalidOperationException("Installed embedding checkpoint format or shared admission contract differs."); }
            }
            await using (var model = Command(connection, transaction, $"SELECT dimensions FROM {_schema}.models WHERE model=@model"))
            {
                Parameter(model, "model", ModelIdentity); var dimensions = await model.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (dimensions is int known && known != Options.Dimensions) { throw new InvalidOperationException("Embedding model dimensions differ from the installed contract."); }
                if (dimensions is null)
                {
                    await using var count = Command(connection, transaction, $"SELECT count(*) FROM {_schema}.models");
                    if ((long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= Options.MaxModels) { throw new SearchBackpressureException(); }
                    await using var insert = Command(connection, transaction, $"INSERT INTO {_schema}.models VALUES(@model,@dimensions)");
                    Parameter(insert, "model", ModelIdentity); Parameter(insert, "dimensions", Options.Dimensions); _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); Volatile.Write(ref _initialized, 1);
        }
        finally { _operations.Release(); }
    }

    private async ValueTask<Claim> ClaimAsync(SearchScope scope, string[] hashes, Guid owner, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        _ = await PruneAsync(connection, transaction, 128, cancellationToken).ConfigureAwait(false);
        var completed = new Dictionary<string, ReadOnlyMemory<float>>(StringComparer.Ordinal); var missing = new List<Pending>(); var added = 0;
        var observed = new Dictionary<string, (byte[]? Payload, bool Complete, bool Active, long Fence)>(StringComparer.Ordinal);
        var hashParameters = string.Join(',', Enumerable.Range(0, hashes.Length).Select(static i => "@hash" + i.ToString(CultureInfo.InvariantCulture)));
        await using (var inspect = Command(connection, transaction, $"SELECT text_hash,payload,expires_at>clock_timestamp(),lease_expires>clock_timestamp(),fence FROM {_schema}.checkpoints WHERE tenant=@tenant AND index_name=@index AND model=@model AND text_hash IN({hashParameters})"))
        {
            Parameter(inspect, "tenant", scope.Tenant); Parameter(inspect, "index", scope.Index); Parameter(inspect, "model", ModelIdentity);
            for (var i = 0; i < hashes.Length; i++) { Parameter(inspect, "hash" + i.ToString(CultureInfo.InvariantCulture), hashes[i]); }
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                observed.Add(reader.GetString(0), (reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1),
                    !reader.IsDBNull(2) && reader.GetBoolean(2), !reader.IsDBNull(3) && reader.GetBoolean(3), reader.GetInt64(4)));
            }
        }
        foreach (var hash in hashes)
        {
            var exists = false; var fence = 1L;
            if (observed.TryGetValue(hash, out var checkpoint))
            {
                exists = true;
                if (checkpoint.Payload is not null && checkpoint.Complete) { completed.Add(hash, Decode(checkpoint.Payload)); continue; }
                if (checkpoint.Active) { throw new SearchBackpressureException(); }
                fence = checked(checkpoint.Fence + 1);
            }
            if (!exists) { added++; }
            missing.Add(new(hash, fence));
        }
        if (missing.Count == 0) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return new(completed, missing); }
        await using (var budget = Command(connection, transaction, $"SELECT records,reserved_bytes,(SELECT count(*) FROM {_schema}.leases WHERE expires_at>clock_timestamp()),(SELECT count(*) FROM {_schema}.leases WHERE tenant=@tenant AND expires_at>clock_timestamp()) FROM {_schema}.metadata WHERE singleton"))
        {
            Parameter(budget, "tenant", scope.Tenant);
            await using var reader = await budget.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (reader.GetInt64(0) + added > Options.MaxRecords || reader.GetInt64(1) + added * Options.Dimensions * 4L > Options.MaxReservedVectorBytes ||
                reader.GetInt64(2) >= Options.MaxActiveProviderBatches || reader.GetInt64(3) >= Options.MaxActiveProviderBatchesPerTenant)
            { throw new SearchBackpressureException(); }
        }
        var values = string.Join(',', Enumerable.Range(0, missing.Count).Select(static i =>
            "(@tenant,@index,@model,@hash" + i.ToString(CultureInfo.InvariantCulture) + ",@dimensions,@fence" + i.ToString(CultureInfo.InvariantCulture) + ",@owner,clock_timestamp()+(@lease * interval '1 second'),NULL,NULL)"));
        await using (var reserve = Command(connection, transaction, $"INSERT INTO {_schema}.checkpoints VALUES {values} ON CONFLICT(tenant,index_name,model,text_hash) DO UPDATE SET fence=EXCLUDED.fence,owner=EXCLUDED.owner,lease_expires=EXCLUDED.lease_expires,expires_at=NULL,payload=NULL"))
        {
            Parameter(reserve, "tenant", scope.Tenant); Parameter(reserve, "index", scope.Index); Parameter(reserve, "model", ModelIdentity);
            Parameter(reserve, "dimensions", Options.Dimensions); Parameter(reserve, "owner", owner); Parameter(reserve, "lease", Options.LeaseDuration.TotalSeconds);
            for (var i = 0; i < missing.Count; i++)
            {
                Parameter(reserve, "hash" + i.ToString(CultureInfo.InvariantCulture), missing[i].Hash);
                Parameter(reserve, "fence" + i.ToString(CultureInfo.InvariantCulture), missing[i].Fence);
            }
            _ = await reserve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var reserve = Command(connection, transaction, $"INSERT INTO {_schema}.leases VALUES(@owner,@tenant,clock_timestamp()+(@lease * interval '1 second')); UPDATE {_schema}.metadata SET records=records+@count,reserved_bytes=reserved_bytes+@bytes WHERE singleton"))
        {
            Parameter(reserve, "owner", owner); Parameter(reserve, "tenant", scope.Tenant); Parameter(reserve, "lease", Options.LeaseDuration.TotalSeconds);
            Parameter(reserve, "count", added); Parameter(reserve, "bytes", added * Options.Dimensions * 4L); _ = await reserve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return new(completed, missing);
    }

    private async ValueTask CompleteAsync(SearchScope scope, Guid owner, List<Pending> missing, byte[][] vectors, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using (var validate = Command(connection, transaction, $"DELETE FROM {_schema}.leases WHERE owner=@owner AND tenant=@tenant AND expires_at>clock_timestamp()"))
        {
            Parameter(validate, "owner", owner); Parameter(validate, "tenant", scope.Tenant);
            if (await validate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) { throw new SearchEmbeddingCheckpointOwnershipException(); }
        }
        var values = string.Join(',', Enumerable.Range(0, missing.Count).Select(static i =>
            "(@hash" + i.ToString(CultureInfo.InvariantCulture) + ",@fence" + i.ToString(CultureInfo.InvariantCulture) + ",@payload" + i.ToString(CultureInfo.InvariantCulture) + ")"));
        await using (var commit = Command(connection, transaction, $"UPDATE {_schema}.checkpoints AS c SET payload=completed.payload,owner=NULL,lease_expires=NULL,expires_at=clock_timestamp()+(@retention * interval '1 second') FROM(VALUES {values}) AS completed(hash,fence,payload) WHERE c.tenant=@tenant AND c.index_name=@index AND c.model=@model AND c.text_hash=completed.hash AND c.owner=@owner AND c.fence=completed.fence AND c.lease_expires>clock_timestamp()"))
        {
            Parameter(commit, "tenant", scope.Tenant); Parameter(commit, "index", scope.Index); Parameter(commit, "model", ModelIdentity);
            Parameter(commit, "retention", Options.Retention.TotalSeconds); Parameter(commit, "owner", owner);
            for (var i = 0; i < missing.Count; i++)
            {
                Parameter(commit, "hash" + i.ToString(CultureInfo.InvariantCulture), missing[i].Hash);
                Parameter(commit, "fence" + i.ToString(CultureInfo.InvariantCulture), missing[i].Fence);
                Parameter(commit, "payload" + i.ToString(CultureInfo.InvariantCulture), vectors[i]);
            }
            if (await commit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != missing.Count) { throw new SearchEmbeddingCheckpointOwnershipException(); }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReleaseAsync(SearchScope scope, Guid owner)
    {
        await using var connection = await _source.OpenConnectionAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
        await LockAsync(connection, transaction, CancellationToken.None).ConfigureAwait(false);
        await using var command = Command(connection, transaction, $"UPDATE {_schema}.checkpoints SET lease_expires=clock_timestamp()-interval '1 second' WHERE tenant=@tenant AND index_name=@index AND model=@model AND owner=@owner; DELETE FROM {_schema}.leases WHERE owner=@owner AND tenant=@tenant");
        Parameter(command, "tenant", scope.Tenant); Parameter(command, "index", scope.Index); Parameter(command, "model", ModelIdentity); Parameter(command, "owner", owner);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false); await transaction.CommitAsync().ConfigureAwait(false);
    }

    public async ValueTask<int> PruneExpiredAsync(int maximumCount = 128, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 10_000) { throw new ArgumentOutOfRangeException(nameof(maximumCount)); }
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var count = await PruneAsync(connection, transaction, maximumCount, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return count;
        }
        finally { _operations.Release(); }
    }
    private async ValueTask<int> PruneAsync(DbConnection connection, DbTransaction transaction, int maximumCount, CancellationToken cancellationToken)
    {
        await using var prune = Command(connection, transaction, $"""
            WITH victims AS(SELECT tenant,index_name,model,text_hash FROM {_schema}.checkpoints WHERE coalesce(expires_at,lease_expires)<=clock_timestamp() ORDER BY coalesce(expires_at,lease_expires),tenant,index_name,model,text_hash LIMIT @limit),
            removed AS(DELETE FROM {_schema}.checkpoints WHERE(tenant,index_name,model,text_hash) IN(SELECT tenant,index_name,model,text_hash FROM victims) RETURNING dimensions),
            totals AS(SELECT count(*) AS count,coalesce(sum(dimensions*4),0) AS bytes FROM removed)
            UPDATE {_schema}.metadata SET records=records-totals.count,reserved_bytes=reserved_bytes-totals.bytes FROM totals WHERE singleton RETURNING totals.count::integer
            """);
        Parameter(prune, "limit", maximumCount); var count = (int)(await prune.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await using var leases = Command(connection, transaction, $"DELETE FROM {_schema}.leases WHERE owner IN(SELECT owner FROM {_schema}.leases WHERE expires_at<=clock_timestamp() ORDER BY expires_at,owner LIMIT @limit)");
        Parameter(leases, "limit", maximumCount); _ = await leases.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); return count;
    }
    private async ValueTask LockAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT singleton FROM {_schema}.metadata WHERE singleton FOR UPDATE");
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) { throw new InvalidOperationException("Initialize embedding checkpoint storage before use."); }
    }
    private DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.CommandTimeout = Options.CommandTimeoutSeconds; return command; }
    private static void Parameter(DbCommand command, string name, object value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
}

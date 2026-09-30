using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BlueTusk.Edge.Sqlite;

public sealed partial class SqliteEdgeStore
{
    private async ValueTask StageOrderedConfirmationAsync(SqliteConnection connection, SqliteTransaction transaction,
        EdgeMutation mutation, CancellationToken cancellationToken)
    {
        if (!EdgeOrderedMutationId.TryParse(mutation.Id, out var stream, out var sequence)) { return; }
        await using (var owner = Command(connection, transaction, "SELECT next_sequence FROM ordered_streams WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND stream_id=@stream"))
        {
            ScopeParameters(owner, mutation.Scope); owner.Parameters.AddWithValue("stream", stream);
            if (await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long next) { return; }
            if (sequence == 0 || sequence >= next) { throw new EdgeMutationIdentityException(); }
        }
        await using (var budget = Command(connection, transaction, "SELECT count(*),coalesce(sum(CASE WHEN confirmed=0 THEN length(payload) ELSE 0 END),0) FROM ordered_confirmations"))
        {
            await using var reader = await budget.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (reader.GetInt64(0) >= Options.MaxReceiptRecords || reader.GetInt64(1) > Options.MaxPendingBytes - mutation.Payload.Length)
            { throw new EdgeCapacityException("The ordered confirmation outbox count or byte budget is full."); }
        }
        await using var insert = Command(connection, transaction, "INSERT INTO ordered_confirmations(tenant,scope_id,epoch,stream_id,sequence,mutation_id,document_id,expected_revision,kind,payload,fingerprint) VALUES(@tenant,@scope,@epoch,@stream,@sequence,@mutation,@id,@expected,@kind,@payload,@fingerprint)");
        MutationParameters(insert, mutation); insert.Parameters.AddWithValue("stream", stream); insert.Parameters.AddWithValue("sequence", sequence);
        insert.Parameters.AddWithValue("id", mutation.DocumentId); insert.Parameters.AddWithValue("expected", mutation.ExpectedRevision);
        insert.Parameters.AddWithValue("kind", (int)mutation.Kind); insert.Parameters.AddWithValue("payload", mutation.Payload.ToArray());
        insert.Parameters.AddWithValue("fingerprint", mutation.Fingerprint);
        _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Allocates the next ordered identity and queues its mutation in the same durable transaction.</summary>
    public async ValueTask<EdgeMutation> EnqueueOrderedAsync(EdgeScope scope, string documentId, long expectedRevision,
        EdgeMutationKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!(await CheckScopeAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false)).SnapshotReady) { throw new EdgeScopeMismatchException(); }
        await CheckNoSnapshotAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        string stream; long sequence;
        await using (var read = Command(connection, transaction, "SELECT stream_id,next_sequence FROM ordered_streams WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch"))
        {
            ScopeParameters(read, scope);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { stream = reader.GetString(0); sequence = reader.GetInt64(1); }
            else { stream = EdgeOrderedMutationId.NewStreamId(); sequence = 1; }
        }
        if (sequence > EdgeOrderedMutationId.MaxSequence) { throw new EdgeCapacityException("The ordered mutation stream is exhausted; activate a new authorized epoch."); }
        var mutation = new EdgeMutation(scope, EdgeOrderedMutationId.Create(stream, sequence), documentId, expectedRevision, kind, payload);
        if (mutation.Payload.Length > Options.MaxRecordBytes) { throw new EdgeCapacityException("Queued payload exceeds the record budget."); }
        await EnqueueInTransactionAsync(connection, transaction, mutation, cancellationToken, orderedAllocation: true).ConfigureAwait(false);
        await using (var advance = Command(connection, transaction, "INSERT INTO ordered_streams(tenant,scope_id,epoch,stream_id,next_sequence) VALUES(@tenant,@scope,@epoch,@stream,@next) ON CONFLICT(tenant,scope_id,epoch) DO UPDATE SET next_sequence=excluded.next_sequence"))
        {
            ScopeParameters(advance, scope); advance.Parameters.AddWithValue("stream", stream); advance.Parameters.AddWithValue("next", sequence + 1);
            _ = await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return mutation;
    }

    public async ValueTask<EdgeMutation?> ReadNextUnconfirmedOrderedReceiptAsync(EdgeScope scope, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await CheckScopeAsync(connection, null, scope, cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, "SELECT mutation_id,document_id,expected_revision,kind,payload FROM ordered_confirmations WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND confirmed=0 ORDER BY sequence LIMIT 1");
        ScopeParameters(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new EdgeMutation(scope, Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), reader.GetInt64(2), (EdgeMutationKind)reader.GetInt32(3), (byte[])reader.GetValue(4))
            : null;
    }

    public async ValueTask MarkOrderedReceiptConfirmedAsync(EdgeMutation mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        _ = await CheckScopeAsync(connection, transaction, mutation.Scope, cancellationToken).ConfigureAwait(false);
        await using (var read = Command(connection, transaction, "SELECT fingerprint FROM ordered_confirmations WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id=@mutation"))
        {
            MutationParameters(read, mutation);
            if (await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string fingerprint || fingerprint != mutation.Fingerprint)
            { throw new EdgeMutationIdentityException(); }
        }
        await using (var mark = Command(connection, transaction, "UPDATE ordered_confirmations SET confirmed=1,payload=X'' WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id=@mutation"))
        { MutationParameters(mark, mutation); _ = await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Guid?> ReadConfirmedOrderedHorizonAsync(EdgeScope scope, int maxReceipts = 1000, CancellationToken cancellationToken = default)
    {
        if (maxReceipts is < 1 or > 10_000) { throw new ArgumentOutOfRangeException(nameof(maxReceipts)); }
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await CheckScopeAsync(connection, null, scope, cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, "SELECT c.sequence,c.mutation_id,c.confirmed,s.horizon FROM ordered_confirmations c JOIN ordered_streams s ON s.tenant=c.tenant AND s.scope_id=c.scope_id AND s.epoch=c.epoch AND s.stream_id=c.stream_id WHERE c.tenant=@tenant AND c.scope_id=@scope AND c.epoch=@epoch AND c.sequence>s.horizon ORDER BY c.sequence LIMIT @maximum");
        ScopeParameters(command, scope); command.Parameters.AddWithValue("maximum", maxReceipts);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        long expected = -1; Guid? through = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (expected == -1) { expected = reader.GetInt64(3) + 1; }
            if (reader.GetInt64(0) != expected || reader.GetInt32(2) == 0) { break; }
            through = Guid.ParseExact(reader.GetString(1), "N"); expected++;
        }
        return through;
    }

    public async ValueTask MarkOrderedReceiptHorizonAsync(EdgeScope scope, Guid throughMutationId, CancellationToken cancellationToken = default)
    {
        if (!EdgeOrderedMutationId.TryParse(throughMutationId, out var stream, out var through) || through == 0) { throw new ArgumentException("An ordered mutation identity is required.", nameof(throughMutationId)); }
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        _ = await CheckScopeAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        long horizon;
        await using (var read = Command(connection, transaction, "SELECT horizon FROM ordered_streams WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND stream_id=@stream"))
        { ScopeParameters(read, scope); read.Parameters.AddWithValue("stream", stream); horizon = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? throw new EdgeMutationIdentityException(), CultureInfo.InvariantCulture); }
        if (through <= horizon) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return; }
        if (through - horizon > 10_000) { throw new EdgeCapacityException("Ordered horizon exceeds the bounded local batch."); }
        await using (var verify = Command(connection, transaction, "SELECT count(*),coalesce(sum(confirmed),0) FROM ordered_confirmations WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND stream_id=@stream AND sequence>@floor AND sequence<=@through"))
        {
            ScopeParameters(verify, scope); verify.Parameters.AddWithValue("stream", stream); verify.Parameters.AddWithValue("floor", horizon); verify.Parameters.AddWithValue("through", through);
            await using var reader = await verify.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (reader.GetInt64(0) != through - horizon || reader.GetInt64(1) != through - horizon) { throw new EdgeRevisionConflictException(); }
        }
        await using (var receipts = Command(connection, transaction, "DELETE FROM receipts WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id IN (SELECT c.mutation_id FROM ordered_confirmations c WHERE c.tenant=@tenant AND c.scope_id=@scope AND c.epoch=@epoch AND c.stream_id=@stream AND c.sequence<=@through) AND NOT EXISTS (SELECT 1 FROM mutations m WHERE m.tenant=receipts.tenant AND m.scope_id=receipts.scope_id AND m.epoch=receipts.epoch AND m.mutation_id=receipts.mutation_id)"))
        { ScopeParameters(receipts, scope); receipts.Parameters.AddWithValue("stream", stream); receipts.Parameters.AddWithValue("through", through); _ = await receipts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await using (var delete = Command(connection, transaction, "DELETE FROM ordered_confirmations WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND stream_id=@stream AND sequence<=@through"))
        { ScopeParameters(delete, scope); delete.Parameters.AddWithValue("stream", stream); delete.Parameters.AddWithValue("through", through); _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await using (var advance = Command(connection, transaction, "UPDATE ordered_streams SET horizon=@through WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND stream_id=@stream"))
        { ScopeParameters(advance, scope); advance.Parameters.AddWithValue("stream", stream); advance.Parameters.AddWithValue("through", through); _ = await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

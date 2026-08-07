using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using BlueTusk.Replication.PgOutput;

namespace BlueTusk.Streams;

internal sealed class PgOutputTransactionAssembler
{
    private readonly ChangeSourceIdentity _source;
    private readonly TransactionAssemblyOptions _options;
    private readonly ITransactionSpool _spool;
    private readonly Dictionary<uint, PendingTransaction> _transactions = [];
    private readonly Dictionary<uint, string> _preparingTransactions = [];
    private readonly Dictionary<uint, ChangeTable> _relations = [];
    private readonly Dictionary<uint, ChangeTypeIdentity> _types = [];
    private uint? _ordinaryTransactionId;

    public PgOutputTransactionAssembler(
        ChangeSourceIdentity source,
        TransactionAssemblyOptions options,
        ITransactionSpool spool)
    {
        _source = source;
        _options = options;
        _spool = spool;
    }

    public async ValueTask<AssembledChangeTransaction?> ProcessAsync(
        BlueTuskPgOutputEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        switch (envelope.Message)
        {
            case BlueTuskPgOutputType type:
                _types[type.TypeId] = new ChangeTypeIdentity(type.TypeId, type.Namespace, type.Name);
                return null;
            case BlueTuskPgOutputRelation relation:
                CacheRelation(relation);
                return null;
            case BlueTuskPgOutputBegin begin:
                BeginOrdinary(begin);
                return null;
            case BlueTuskPgOutputOrigin origin:
                RequireOrdinary().Origin = origin.Name;
                return null;
            case BlueTuskPgOutputInsert insert:
                await AppendInsertAsync(
                    insert,
                    Estimate(envelope),
                    envelope.OwnsPayload,
                    cancellationToken).ConfigureAwait(false);
                return null;
            case BlueTuskPgOutputUpdate update:
                await AppendUpdateAsync(
                    update,
                    Estimate(envelope),
                    envelope.OwnsPayload,
                    cancellationToken).ConfigureAwait(false);
                return null;
            case BlueTuskPgOutputDelete delete:
                await AppendDeleteAsync(
                    delete,
                    Estimate(envelope),
                    envelope.OwnsPayload,
                    cancellationToken).ConfigureAwait(false);
                return null;
            case BlueTuskPgOutputTruncate truncate:
                await AppendTruncateAsync(truncate, Estimate(envelope), cancellationToken).ConfigureAwait(false);
                return null;
            case BlueTuskPgOutputLogicalMessage message:
                return await AppendLogicalMessageAsync(message, envelope, cancellationToken).ConfigureAwait(false);
            case BlueTuskPgOutputCommit commit:
                return await CommitOrdinaryAsync(commit, cancellationToken).ConfigureAwait(false);
            case BlueTuskPgOutputStreamStart start:
                BeginStream(start, envelope.XLogData.ServerClock);
                return null;
            case BlueTuskPgOutputStreamStop:
                return null;
            case BlueTuskPgOutputStreamCommit commit:
                return await CommitStreamAsync(commit, cancellationToken).ConfigureAwait(false);
            case BlueTuskPgOutputStreamAbort abort:
                await AbortAsync(abort.TransactionId, cancellationToken).ConfigureAwait(false);
                return null;
            case BlueTuskPgOutputBeginPrepare beginPrepare:
                RequirePreparedTransactionStaging();
                BeginPrepared(beginPrepare);
                return null;
            case BlueTuskPgOutputPrepare prepare:
                RequirePreparedTransactionStaging();
                return await PrepareOrdinaryAsync(prepare, cancellationToken).ConfigureAwait(false);
            case BlueTuskPgOutputCommitPrepared commitPrepared:
                RequirePreparedTransactionStaging();
                return CreateCommitPrepared(commitPrepared);
            case BlueTuskPgOutputRollbackPrepared rollbackPrepared:
                RequirePreparedTransactionStaging();
                return CreateRollbackPrepared(rollbackPrepared);
            case BlueTuskPgOutputStreamPrepare streamPrepare:
                RequirePreparedTransactionStaging();
                return await PrepareStreamAsync(streamPrepare, cancellationToken).ConfigureAwait(false);
            default:
                throw new TransactionAssemblyException(
                    $"Unsupported pgoutput message type {envelope.Message.GetType().Name}.");
        }
    }

    public async ValueTask AbortAllAsync(CancellationToken cancellationToken)
    {
        foreach (var transaction in _transactions.Values)
        {
            await transaction.AbortAsync(cancellationToken).ConfigureAwait(false);
        }

        _transactions.Clear();
        _preparingTransactions.Clear();
        _ordinaryTransactionId = null;
    }

    private void CacheRelation(BlueTuskPgOutputRelation relation)
    {
        var columns = new ChangeColumn[relation.Columns.Count];
        for (var ordinal = 0; ordinal < columns.Length; ordinal++)
        {
            var column = relation.Columns[ordinal];
            columns[ordinal] = new ChangeColumn(
                ordinal,
                column.Name,
                column.TypeOid,
                column.TypeModifier,
                (column.Options & BlueTuskPgOutputRelationColumnOptions.Key) != 0,
                _types.GetValueOrDefault(column.TypeOid));
        }

        _relations[relation.RelationId] = ChangeTable.CreateOwned(
            relation.RelationId,
            relation.Namespace,
            relation.Name,
            relation.ReplicaIdentity,
            columns);
    }

    private void BeginOrdinary(BlueTuskPgOutputBegin begin)
        => BeginOrdinary(
            begin.TransactionId,
            begin.FinalPosition,
            begin.CommitTimestamp);

    private void BeginPrepared(BlueTuskPgOutputBeginPrepare begin)
    {
        ValidateGlobalTransactionId(begin.GlobalTransactionId);
        if (_preparingTransactions.ContainsKey(begin.TransactionId))
        {
            throw new TransactionAssemblyException(
                $"Prepared transaction {begin.TransactionId} has already begun.");
        }

        BeginOrdinary(
            begin.TransactionId,
            begin.PreparePosition,
            begin.PrepareTimestamp);
        _preparingTransactions.Add(begin.TransactionId, begin.GlobalTransactionId);
    }

    private void BeginOrdinary(
        uint transactionId,
        BlueTuskLogSequenceNumber finalPosition,
        DateTimeOffset timestamp)
    {
        if (_ordinaryTransactionId.HasValue)
        {
            throw new TransactionAssemblyException(
                $"Transaction {_ordinaryTransactionId.Value} is still active when transaction {transactionId} begins.");
        }

        var transaction = CreateTransaction(
            transactionId,
            finalPosition,
            timestamp);
        if (!_transactions.TryAdd(transactionId, transaction))
        {
            throw new TransactionAssemblyException($"Transaction {transactionId} has already begun.");
        }

        _ordinaryTransactionId = transactionId;
    }

    private void BeginStream(BlueTuskPgOutputStreamStart start, DateTimeOffset serverClock)
    {
        if (start.IsFirstSegment)
        {
            if (!_transactions.TryAdd(
                    start.TransactionId,
                    CreateTransaction(start.TransactionId, BlueTuskLogSequenceNumber.Zero, serverClock)))
            {
                throw new TransactionAssemblyException(
                    $"Streamed transaction {start.TransactionId} has already begun.");
            }

            return;
        }

        if (!_transactions.ContainsKey(start.TransactionId))
        {
            throw new TransactionAssemblyException(
                $"A continuation arrived for unknown streamed transaction {start.TransactionId}.");
        }
    }

    private PendingTransaction CreateTransaction(
        uint transactionId,
        BlueTuskLogSequenceNumber beginFinalPosition,
        DateTimeOffset beginTimestamp) =>
        new(_source, transactionId, beginFinalPosition, beginTimestamp, _options, _spool);

    private async ValueTask AppendInsertAsync(
        BlueTuskPgOutputInsert insert,
        long estimatedBytes,
        bool ownsPayload,
        CancellationToken cancellationToken)
    {
        var transaction = ResolveTransaction(insert.StreamingTransactionId);
        var table = ResolveTable(insert.RelationId);
        await transaction.AppendAsync(
            new PendingChange
            {
                Kind = PendingChangeKind.Insert,
                TableToken = transaction.GetTableToken(table),
                NewRow = CreatePendingTuple(insert.NewRow, ownsPayload),
            },
            estimatedBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendUpdateAsync(
        BlueTuskPgOutputUpdate update,
        long estimatedBytes,
        bool ownsPayload,
        CancellationToken cancellationToken)
    {
        var transaction = ResolveTransaction(update.StreamingTransactionId);
        var table = ResolveTable(update.RelationId);
        await transaction.AppendAsync(
            new PendingChange
            {
                Kind = PendingChangeKind.Update,
                TableToken = transaction.GetTableToken(table),
                OldRowKind = update.OldRowKind,
                OldRow = update.OldRow is null
                    ? null
                    : CreatePendingTuple(update.OldRow, ownsPayload),
                NewRow = CreatePendingTuple(update.NewRow, ownsPayload),
            },
            estimatedBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendDeleteAsync(
        BlueTuskPgOutputDelete delete,
        long estimatedBytes,
        bool ownsPayload,
        CancellationToken cancellationToken)
    {
        var transaction = ResolveTransaction(delete.StreamingTransactionId);
        var table = ResolveTable(delete.RelationId);
        await transaction.AppendAsync(
            new PendingChange
            {
                Kind = PendingChangeKind.Delete,
                TableToken = transaction.GetTableToken(table),
                OldRowKind = delete.OldRowKind,
                OldRow = CreatePendingTuple(delete.OldRow, ownsPayload),
            },
            estimatedBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendTruncateAsync(
        BlueTuskPgOutputTruncate truncate,
        long estimatedBytes,
        CancellationToken cancellationToken)
    {
        var transaction = ResolveTransaction(truncate.StreamingTransactionId);
        var tokens = truncate.RelationIds
            .Select(relationId => transaction.GetTableToken(ResolveTable(relationId)))
            .ToArray();
        await transaction.AppendAsync(
            new PendingChange
            {
                Kind = PendingChangeKind.Truncate,
                TableTokens = tokens,
                TruncateOptions = truncate.Options,
            },
            estimatedBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AssembledChangeTransaction?> AppendLogicalMessageAsync(
        BlueTuskPgOutputLogicalMessage message,
        BlueTuskPgOutputEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var pending = new PendingChange
        {
            Kind = PendingChangeKind.LogicalMessage,
            IsTransactionalMessage = message.IsTransactional,
            MessagePosition = message.Position,
            MessagePrefix = message.Prefix,
            MessageContent = envelope.OwnsPayload
                ? message.Content
                : message.Content.ToArray(),
        };

        if (message.IsTransactional || message.StreamingTransactionId.HasValue || _ordinaryTransactionId.HasValue)
        {
            await ResolveTransaction(message.StreamingTransactionId)
                .AppendAsync(pending, Estimate(envelope), cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        var changes = new[] { pending };
        var tables = Array.Empty<ChangeTable>();
        var changeSet = CreateMemoryChangeSet(
            changes,
            tables,
            transactionId: 0,
            message.Position,
            Estimate(envelope));
        var transaction = new ChangeTransaction(
            _source,
            0,
            message.Position,
            message.Position,
            message.Position,
            envelope.XLogData.ServerClock,
            origin: null,
            isSynthetic: true,
            ChangeTransactionOutcome.Committed,
            globalTransactionId: null,
            changeSet);
        return new AssembledChangeTransaction(transaction, release: null);
    }

    private async ValueTask<AssembledChangeTransaction> CommitOrdinaryAsync(
        BlueTuskPgOutputCommit commit,
        CancellationToken cancellationToken)
    {
        var transactionId = _ordinaryTransactionId ?? throw new TransactionAssemblyException(
            "A commit arrived without an active ordinary transaction.");
        if (_preparingTransactions.ContainsKey(transactionId))
        {
            throw new TransactionAssemblyException(
                $"Prepared transaction {transactionId} ended with an ordinary commit message.");
        }

        _ordinaryTransactionId = null;
        return await CommitAsync(
            transactionId,
            commit.CommitPosition,
            commit.TransactionEndPosition,
            commit.CommitTimestamp,
            ChangeTransactionOutcome.Committed,
            globalTransactionId: null,
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<AssembledChangeTransaction> CommitStreamAsync(
        BlueTuskPgOutputStreamCommit commit,
        CancellationToken cancellationToken) =>
        CommitAsync(
            commit.TransactionId,
            commit.CommitPosition,
            commit.TransactionEndPosition,
            commit.CommitTimestamp,
            ChangeTransactionOutcome.Committed,
            globalTransactionId: null,
            cancellationToken);

    private async ValueTask<AssembledChangeTransaction> PrepareOrdinaryAsync(
        BlueTuskPgOutputPrepare prepare,
        CancellationToken cancellationToken)
    {
        var transactionId = _ordinaryTransactionId ?? throw new TransactionAssemblyException(
            "A prepare arrived without an active ordinary transaction.");
        if (transactionId != prepare.TransactionId)
        {
            throw new TransactionAssemblyException(
                $"Prepare for transaction {prepare.TransactionId} arrived while transaction {transactionId} is active.");
        }

        ValidatePreparingTransaction(prepare.TransactionId, prepare.GlobalTransactionId);
        _ordinaryTransactionId = null;
        _preparingTransactions.Remove(prepare.TransactionId);
        return await CommitAsync(
            prepare.TransactionId,
            prepare.PreparePosition,
            prepare.TransactionEndPosition,
            prepare.PrepareTimestamp,
            ChangeTransactionOutcome.Prepared,
            prepare.GlobalTransactionId,
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<AssembledChangeTransaction> PrepareStreamAsync(
        BlueTuskPgOutputStreamPrepare prepare,
        CancellationToken cancellationToken)
    {
        ValidateGlobalTransactionId(prepare.GlobalTransactionId);
        return CommitAsync(
            prepare.TransactionId,
            prepare.PreparePosition,
            prepare.TransactionEndPosition,
            prepare.PrepareTimestamp,
            ChangeTransactionOutcome.Prepared,
            prepare.GlobalTransactionId,
            cancellationToken);
    }

    private AssembledChangeTransaction CreateCommitPrepared(BlueTuskPgOutputCommitPrepared commit)
    {
        ValidateGlobalTransactionId(commit.GlobalTransactionId);
        RejectIncompletePreparedFinalization(commit.TransactionId);
        return CreateLifecycleTransaction(
            commit.TransactionId,
            commit.CommitPosition,
            commit.CommitPosition,
            commit.TransactionEndPosition,
            commit.CommitTimestamp,
            ChangeTransactionOutcome.Committed,
            commit.GlobalTransactionId);
    }

    private AssembledChangeTransaction CreateRollbackPrepared(BlueTuskPgOutputRollbackPrepared rollback)
    {
        ValidateGlobalTransactionId(rollback.GlobalTransactionId);
        RejectIncompletePreparedFinalization(rollback.TransactionId);
        return CreateLifecycleTransaction(
            rollback.TransactionId,
            rollback.PreparedTransactionEndPosition,
            rollback.PreparedTransactionEndPosition,
            rollback.RollbackEndPosition,
            rollback.RollbackTimestamp,
            ChangeTransactionOutcome.RolledBack,
            rollback.GlobalTransactionId);
    }

    private AssembledChangeTransaction CreateLifecycleTransaction(
        uint transactionId,
        BlueTuskLogSequenceNumber beginFinalPosition,
        BlueTuskLogSequenceNumber commitPosition,
        BlueTuskLogSequenceNumber commitEndPosition,
        DateTimeOffset commitTimestamp,
        ChangeTransactionOutcome outcome,
        string globalTransactionId)
    {
        var changeSet = CreateMemoryChangeSet(
            [],
            [],
            transactionId,
            commitEndPosition,
            estimatedBytes: 0);
        var transaction = new ChangeTransaction(
            _source,
            transactionId,
            beginFinalPosition,
            commitPosition,
            commitEndPosition,
            commitTimestamp,
            origin: null,
            isSynthetic: false,
            outcome,
            globalTransactionId,
            changeSet);
        return new AssembledChangeTransaction(transaction, release: null);
    }

    private async ValueTask<AssembledChangeTransaction> CommitAsync(
        uint transactionId,
        BlueTuskLogSequenceNumber commitPosition,
        BlueTuskLogSequenceNumber commitEndPosition,
        DateTimeOffset commitTimestamp,
        ChangeTransactionOutcome outcome,
        string? globalTransactionId,
        CancellationToken cancellationToken)
    {
        if (!_transactions.TryGetValue(transactionId, out var pending))
        {
            throw new TransactionAssemblyException($"A commit arrived for unknown transaction {transactionId}.");
        }

        var completed = await pending.CompleteAsync(cancellationToken).ConfigureAwait(false);
        _transactions.Remove(transactionId);
        ChangeSet changeSet;
        Func<ValueTask>? release = null;
        if (completed.SpoolReader is null)
        {
            changeSet = CreateMemoryChangeSet(
                completed.InMemoryChanges,
                completed.Tables,
                transactionId,
                commitEndPosition,
                completed.EstimatedBytes);
        }
        else
        {
            changeSet = CreateSpoolChangeSet(
                completed.SpoolReader,
                completed.Tables,
                completed.Count,
                completed.EstimatedBytes,
                transactionId,
                commitEndPosition);
            release = completed.SpoolReader.DisposeAsync;
        }

        var transaction = new ChangeTransaction(
            _source,
            transactionId,
            pending.BeginFinalPosition,
            commitPosition,
            commitEndPosition,
            commitTimestamp,
            pending.Origin,
            isSynthetic: false,
            outcome,
            globalTransactionId,
            changeSet);
        return new AssembledChangeTransaction(transaction, release);
    }

    private async ValueTask AbortAsync(uint transactionId, CancellationToken cancellationToken)
    {
        if (!_transactions.Remove(transactionId, out var transaction))
        {
            throw new TransactionAssemblyException($"An abort arrived for unknown transaction {transactionId}.");
        }

        _preparingTransactions.Remove(transactionId);
        if (_ordinaryTransactionId == transactionId)
        {
            _ordinaryTransactionId = null;
        }

        await transaction.AbortAsync(cancellationToken).ConfigureAwait(false);
    }

    private PendingTransaction ResolveTransaction(uint? streamingTransactionId)
    {
        var transactionId = streamingTransactionId ?? _ordinaryTransactionId ?? throw new TransactionAssemblyException(
            "A transactional change arrived outside a transaction.");
        if (!_transactions.TryGetValue(transactionId, out var transaction))
        {
            throw new TransactionAssemblyException($"A change arrived for unknown transaction {transactionId}.");
        }

        return transaction;
    }

    private PendingTransaction RequireOrdinary() => ResolveTransaction(streamingTransactionId: null);

    private void RequirePreparedTransactionStaging()
    {
        if (_options.PreparedTransactionMode != PreparedTransactionMode.Stage)
        {
            throw new PreparedTransactionNotSupportedException();
        }
    }

    private void ValidatePreparingTransaction(uint transactionId, string globalTransactionId)
    {
        ValidateGlobalTransactionId(globalTransactionId);
        if (!_preparingTransactions.TryGetValue(transactionId, out var expectedGlobalTransactionId))
        {
            throw new TransactionAssemblyException(
                $"Prepare arrived for transaction {transactionId} without a matching begin-prepare message.");
        }

        if (!string.Equals(expectedGlobalTransactionId, globalTransactionId, StringComparison.Ordinal))
        {
            throw new TransactionAssemblyException(
                $"Prepare for transaction {transactionId} changed its global transaction ID.");
        }
    }

    private static void ValidateGlobalTransactionId(string globalTransactionId)
    {
        if (string.IsNullOrWhiteSpace(globalTransactionId))
        {
            throw new TransactionAssemblyException("A prepared transaction has an empty global transaction ID.");
        }
    }

    private void RejectIncompletePreparedFinalization(uint transactionId)
    {
        if (_transactions.ContainsKey(transactionId) || _preparingTransactions.ContainsKey(transactionId))
        {
            throw new TransactionAssemblyException(
                $"Prepared transaction {transactionId} was finalized before its prepare delivery completed.");
        }
    }

    private ChangeTable ResolveTable(uint relationId) =>
        _relations.TryGetValue(relationId, out var table)
            ? table
            : throw new TransactionAssemblyException(
                $"A change references relation {relationId} before its metadata was received.");

    private static PendingTuple CreatePendingTuple(
        BlueTuskPgOutputTuple tuple,
        bool ownsPayload)
    {
        var values = new PendingTupleValue[tuple.Values.Count];
        for (var index = 0; index < values.Length; index++)
        {
            var value = tuple.Values[index];
            values[index] = new PendingTupleValue(
                value.Kind,
                ownsPayload ? value.Data : value.Data.ToArray());
        }

        return new PendingTuple(values);
    }

    private static long Estimate(BlueTuskPgOutputEnvelope envelope)
    {
        if (!envelope.XLogData.Data.IsEmpty)
        {
            return envelope.XLogData.Data.Length + 64L;
        }

        return envelope.Message switch
        {
            BlueTuskPgOutputInsert insert => Estimate(insert.NewRow),
            BlueTuskPgOutputUpdate update => Estimate(update.NewRow) + (update.OldRow is null ? 0 : Estimate(update.OldRow)),
            BlueTuskPgOutputDelete delete => Estimate(delete.OldRow),
            BlueTuskPgOutputLogicalMessage message => message.Content.Length + message.Prefix.Length * 2L + 64,
            BlueTuskPgOutputTruncate truncate => truncate.RelationIds.Count * sizeof(uint) + 64L,
            _ => 64,
        };
    }

    private static long Estimate(BlueTuskPgOutputTuple tuple) =>
        tuple.Values.Sum(value => value.Data.Length + 8L) + 64;

    private ChangeSet CreateMemoryChangeSet(
        PendingChange[] changes,
        ChangeTable[] tables,
        uint transactionId,
        BlueTuskLogSequenceNumber commitEndPosition,
        long estimatedBytes) =>
        new(
            changes.Length,
            estimatedBytes,
            isSpooled: false,
            cancellationToken => ReadMemoryChangesAsync(
                changes,
                tables,
                transactionId,
                commitEndPosition,
                cancellationToken));

    private ChangeSet CreateSpoolChangeSet(
        ITransactionSpoolReader reader,
        ChangeTable[] tables,
        int count,
        long estimatedBytes,
        uint transactionId,
        BlueTuskLogSequenceNumber commitEndPosition) =>
        new(
            count,
            estimatedBytes,
            isSpooled: true,
            cancellationToken => ReadSpoolChangesAsync(
                reader,
                tables,
                transactionId,
                commitEndPosition,
                cancellationToken));

    private async IAsyncEnumerable<Change> ReadMemoryChangesAsync(
        PendingChange[] changes,
        ChangeTable[] tables,
        uint transactionId,
        BlueTuskLogSequenceNumber commitEndPosition,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        for (var ordinal = 0; ordinal < changes.Length; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return MaterializeChange(changes[ordinal], tables, transactionId, commitEndPosition, ordinal);
        }
    }

    private async IAsyncEnumerable<Change> ReadSpoolChangesAsync(
        ITransactionSpoolReader reader,
        ChangeTable[] tables,
        uint transactionId,
        BlueTuskLogSequenceNumber commitEndPosition,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var ordinal = 0;
        await foreach (var record in reader.ReadRecordsAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return MaterializeChange(
                PendingChangeCodec.Deserialize(record),
                tables,
                transactionId,
                commitEndPosition,
                ordinal++);
        }
    }

    private Change MaterializeChange(
        PendingChange change,
        ChangeTable[] tables,
        uint transactionId,
        BlueTuskLogSequenceNumber commitEndPosition,
        int ordinal)
    {
        var id = new ChangeId(_source, commitEndPosition, transactionId, ordinal);
        return change.Kind switch
        {
            PendingChangeKind.Insert => new InsertChange(
                id,
                BuildRow(ResolveTableToken(tables, change.TableToken), change.NewRow!, oldRowKind: null)),
            PendingChangeKind.Update => MaterializeUpdate(id, change, tables),
            PendingChangeKind.Delete => new DeleteChange(
                id,
                BuildRow(ResolveTableToken(tables, change.TableToken), change.OldRow!, change.OldRowKind)),
            PendingChangeKind.Truncate => new TruncateChange(
                id,
                new ReadOnlyCollection<ChangeTable>(
                    change.TableTokens.Select(token => ResolveTableToken(tables, token)).ToArray()),
                (change.TruncateOptions & BlueTuskPgOutputTruncateOptions.Cascade) != 0,
                (change.TruncateOptions & BlueTuskPgOutputTruncateOptions.RestartIdentity) != 0),
            PendingChangeKind.LogicalMessage => new LogicalMessageChange(
                id,
                change.IsTransactionalMessage,
                change.MessagePosition,
                change.MessagePrefix!,
                change.MessageContent),
            _ => throw new TransactionSpoolIntegrityException($"Unknown pending change kind {change.Kind}."),
        };
    }

    private static UpdateChange MaterializeUpdate(ChangeId id, PendingChange change, ChangeTable[] tables)
    {
        var table = ResolveTableToken(tables, change.TableToken);
        var oldRow = change.OldRow is null
            ? BuildUnavailableOldRow(table)
            : BuildRow(table, change.OldRow, change.OldRowKind);
        var newRow = BuildRow(table, change.NewRow!, oldRowKind: null);
        var isExact = change.OldRowKind == BlueTuskPgOutputOldRowKind.Full &&
                      change.OldRow?.Values.Length == table.Columns.Count &&
                      change.NewRow!.Values.Length == table.Columns.Count;
        var changed = new List<int>();
        if (isExact)
        {
            for (var index = 0; index < table.Columns.Count; index++)
            {
                if (newRow[index].State == ChangeColumnState.UnchangedToast)
                {
                    continue;
                }

                if (!newRow[index].Equals(oldRow[index]))
                {
                    changed.Add(index);
                }
            }
        }

        return new UpdateChange(id, oldRow, newRow, new ChangedColumnSet(isExact, changed));
    }

    private static ChangeRow BuildUnavailableOldRow(ChangeTable table) =>
        ChangeRow.CreateOwned(
            table,
            CreateFilledValues(table.Columns.Count, ChangeColumnValue.OldValueUnavailable));

    private static ChangeRow BuildRow(
        ChangeTable table,
        PendingTuple tuple,
        BlueTuskPgOutputOldRowKind? oldRowKind)
    {
        var unavailable = oldRowKind.HasValue
            ? ChangeColumnValue.OldValueUnavailable
            : ChangeColumnValue.NotPublished;
        var values = CreateFilledValues(table.Columns.Count, unavailable);
        if (oldRowKind == BlueTuskPgOutputOldRowKind.Key)
        {
            var keys = table.KeyOrdinals;
            if (tuple.Values.Length == table.Columns.Count)
            {
                foreach (var keyOrdinal in keys)
                {
                    values[keyOrdinal] = MaterializeValue(tuple.Values[keyOrdinal]);
                }
            }
            else if (tuple.Values.Length <= keys.Length)
            {
                for (var index = 0; index < tuple.Values.Length; index++)
                {
                    values[keys[index]] = MaterializeValue(tuple.Values[index]);
                }
            }
            else
            {
                throw new TransactionAssemblyException(
                    $"A key tuple for {table} contains more values than the relation's replica-identity key.");
            }
        }
        else
        {
            if (tuple.Values.Length > values.Length)
            {
                throw new TransactionAssemblyException(
                    $"A tuple for {table} contains more values than its relation metadata.");
            }

            for (var index = 0; index < tuple.Values.Length; index++)
            {
                values[index] = MaterializeValue(tuple.Values[index]);
            }
        }

        return ChangeRow.CreateOwned(table, values);
    }

    private static ChangeColumnValue[] CreateFilledValues(int count, ChangeColumnValue value)
    {
        var values = new ChangeColumnValue[count];
        Array.Fill(values, value);
        return values;
    }

    private static ChangeColumnValue MaterializeValue(PendingTupleValue value) => value.Kind switch
    {
        BlueTuskPgOutputTupleValueKind.Null => ChangeColumnValue.DatabaseNull,
        BlueTuskPgOutputTupleValueKind.UnchangedToast => ChangeColumnValue.UnchangedToast,
        BlueTuskPgOutputTupleValueKind.Text =>
            ChangeColumnValue.FromOwnedValue(value.Data, ChangeValueEncoding.Text),
        BlueTuskPgOutputTupleValueKind.Binary =>
            ChangeColumnValue.FromOwnedValue(value.Data, ChangeValueEncoding.Binary),
        _ => throw new TransactionAssemblyException($"Unknown pgoutput tuple value kind {value.Kind}."),
    };

    private static ChangeTable ResolveTableToken(ChangeTable[] tables, int token)
    {
        if ((uint)token >= (uint)tables.Length)
        {
            throw new TransactionSpoolIntegrityException($"A transaction references unknown table token {token}.");
        }

        return tables[token];
    }
}

internal sealed class AssembledChangeTransaction
{
    private readonly Func<ValueTask>? _release;
    private int _released;

    public AssembledChangeTransaction(ChangeTransaction transaction, Func<ValueTask>? release)
    {
        Transaction = transaction;
        _release = release;
    }

    public ChangeTransaction Transaction { get; }

    public async ValueTask ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0 && _release is not null)
        {
            await _release().ConfigureAwait(false);
        }
    }
}

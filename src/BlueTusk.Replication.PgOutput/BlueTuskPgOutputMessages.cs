namespace BlueTusk.Replication.PgOutput;

/// <summary>A decoded message emitted by PostgreSQL's pgoutput plugin.</summary>
public abstract record BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode Code);

public sealed record BlueTuskPgOutputBegin(
    BlueTuskLogSequenceNumber FinalPosition,
    DateTimeOffset CommitTimestamp,
    uint TransactionId)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.Begin);

public sealed record BlueTuskPgOutputCommit(
    BlueTuskLogSequenceNumber CommitPosition,
    BlueTuskLogSequenceNumber TransactionEndPosition,
    DateTimeOffset CommitTimestamp)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.Commit);

public sealed record BlueTuskPgOutputOrigin(
    BlueTuskLogSequenceNumber CommitPosition,
    string Name)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.Origin);

[Flags]
public enum BlueTuskPgOutputRelationColumnOptions : byte
{
    None = 0,
    Key = 1,
}

public sealed record BlueTuskPgOutputRelationColumn(
    BlueTuskPgOutputRelationColumnOptions Options,
    string Name,
    uint TypeOid,
    int TypeModifier);

public sealed record BlueTuskPgOutputRelation(
    uint? StreamingTransactionId,
    uint RelationId,
    string Namespace,
    string Name,
    char ReplicaIdentity,
    IReadOnlyList<BlueTuskPgOutputRelationColumn> Columns)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.Relation);

public sealed record BlueTuskPgOutputType(
    uint? StreamingTransactionId,
    uint TypeId,
    string Namespace,
    string Name)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.Type);

public enum BlueTuskPgOutputTupleValueKind : byte
{
    Null = (byte)'n',
    UnchangedToast = (byte)'u',
    Text = (byte)'t',
    Binary = (byte)'b',
}

public readonly record struct BlueTuskPgOutputTupleValue(
    BlueTuskPgOutputTupleValueKind Kind,
    ReadOnlyMemory<byte> Data);

public sealed record BlueTuskPgOutputTuple(
    IReadOnlyList<BlueTuskPgOutputTupleValue> Values);

public sealed record BlueTuskPgOutputInsert(
    uint? StreamingTransactionId,
    uint RelationId,
    BlueTuskPgOutputTuple NewRow)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.Insert);

public enum BlueTuskPgOutputOldRowKind
{
    Key,
    Full,
}

public sealed record BlueTuskPgOutputUpdate(
    uint? StreamingTransactionId,
    uint RelationId,
    BlueTuskPgOutputOldRowKind? OldRowKind,
    BlueTuskPgOutputTuple? OldRow,
    BlueTuskPgOutputTuple NewRow)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.Update);

public sealed record BlueTuskPgOutputDelete(
    uint? StreamingTransactionId,
    uint RelationId,
    BlueTuskPgOutputOldRowKind OldRowKind,
    BlueTuskPgOutputTuple OldRow)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.Delete);

[Flags]
public enum BlueTuskPgOutputTruncateOptions : byte
{
    None = 0,
    Cascade = 1,
    RestartIdentity = 2,
}

public sealed record BlueTuskPgOutputTruncate(
    uint? StreamingTransactionId,
    BlueTuskPgOutputTruncateOptions Options,
    IReadOnlyList<uint> RelationIds)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.Truncate);

public sealed record BlueTuskPgOutputLogicalMessage(
    uint? StreamingTransactionId,
    bool IsTransactional,
    BlueTuskLogSequenceNumber Position,
    string Prefix,
    ReadOnlyMemory<byte> Content)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.Message);

public sealed record BlueTuskPgOutputStreamStart(
    uint TransactionId,
    bool IsFirstSegment)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.StreamStart);

public sealed record BlueTuskPgOutputStreamStop()
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.StreamStop);

public sealed record BlueTuskPgOutputStreamCommit(
    uint TransactionId,
    BlueTuskLogSequenceNumber CommitPosition,
    BlueTuskLogSequenceNumber TransactionEndPosition,
    DateTimeOffset CommitTimestamp)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.StreamCommit);

public sealed record BlueTuskPgOutputStreamAbort(
    uint TransactionId,
    uint SubtransactionId,
    BlueTuskLogSequenceNumber? AbortPosition,
    DateTimeOffset? AbortTimestamp)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.StreamAbort);

public sealed record BlueTuskPgOutputBeginPrepare(
    BlueTuskLogSequenceNumber PreparePosition,
    BlueTuskLogSequenceNumber TransactionEndPosition,
    DateTimeOffset PrepareTimestamp,
    uint TransactionId,
    string GlobalTransactionId)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.BeginPrepare);

public sealed record BlueTuskPgOutputPrepare(
    BlueTuskLogSequenceNumber PreparePosition,
    BlueTuskLogSequenceNumber TransactionEndPosition,
    DateTimeOffset PrepareTimestamp,
    uint TransactionId,
    string GlobalTransactionId)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.Prepare);

public sealed record BlueTuskPgOutputCommitPrepared(
    BlueTuskLogSequenceNumber CommitPosition,
    BlueTuskLogSequenceNumber TransactionEndPosition,
    DateTimeOffset CommitTimestamp,
    uint TransactionId,
    string GlobalTransactionId)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.CommitPrepared);

public sealed record BlueTuskPgOutputRollbackPrepared(
    BlueTuskLogSequenceNumber PreparedTransactionEndPosition,
    BlueTuskLogSequenceNumber RollbackEndPosition,
    DateTimeOffset PrepareTimestamp,
    DateTimeOffset RollbackTimestamp,
    uint TransactionId,
    string GlobalTransactionId)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.RollbackPrepared);

public sealed record BlueTuskPgOutputStreamPrepare(
    BlueTuskLogSequenceNumber PreparePosition,
    BlueTuskLogSequenceNumber TransactionEndPosition,
    DateTimeOffset PrepareTimestamp,
    uint TransactionId,
    string GlobalTransactionId)
    : BlueTuskPgOutputMessage(BlueTuskPgOutputMessageCode.StreamPrepare);

/// <summary>A decoded pgoutput message together with its WAL envelope.</summary>
public sealed record BlueTuskPgOutputEnvelope(
    BlueTuskXLogData XLogData,
    BlueTuskPgOutputMessage Message)
{
    internal bool OwnsPayload => XLogData.OwnsData;

    internal static BlueTuskPgOutputEnvelope CreateOwned(
        BlueTuskXLogData xLogData,
        BlueTuskPgOutputMessage message) =>
        new(xLogData.MarkDataOwned(), message);
}

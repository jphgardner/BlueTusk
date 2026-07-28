namespace BlueTusk.Replication.PgOutput;

public enum BlueTuskPgOutputMessageCode : byte
{
    Begin = (byte)'B',
    Commit = (byte)'C',
    Insert = (byte)'I',
    Update = (byte)'U',
    Delete = (byte)'D',
    Truncate = (byte)'T',
    Relation = (byte)'R',
    Type = (byte)'Y',
    Origin = (byte)'O',
    Message = (byte)'M',
    StreamStart = (byte)'S',
    StreamStop = (byte)'E',
    StreamCommit = (byte)'c',
    StreamAbort = (byte)'A',
    BeginPrepare = (byte)'b',
    Prepare = (byte)'P',
    CommitPrepared = (byte)'K',
    RollbackPrepared = (byte)'r',
    StreamPrepare = (byte)'p',
}

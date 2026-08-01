namespace BlueTusk.Replication;

/// <summary>Indicates malformed data in the PostgreSQL streaming replication protocol.</summary>
public sealed class BlueTuskReplicationProtocolException : Exception
{
    public BlueTuskReplicationProtocolException(string message)
        : base(message)
    {
    }

    public BlueTuskReplicationProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Indicates that a durable checkpoint cannot safely resume from the selected slot.</summary>
public sealed class BlueTuskReplicationCheckpointException : InvalidOperationException
{
    public BlueTuskReplicationCheckpointException(string message)
        : base(message)
    {
    }
}

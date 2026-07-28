namespace BlueTusk.Replication.PgOutput;

/// <summary>Indicates malformed or incompatible pgoutput protocol data.</summary>
public sealed class BlueTuskPgOutputProtocolException : Exception
{
    public BlueTuskPgOutputProtocolException(string message)
        : base(message)
    {
    }

    public BlueTuskPgOutputProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

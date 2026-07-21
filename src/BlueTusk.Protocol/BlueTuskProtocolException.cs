namespace BlueTusk.Protocol;

/// <summary>Represents a malformed or invalid PostgreSQL protocol exchange.</summary>
public sealed class BlueTuskProtocolException : Exception
{
    public BlueTuskProtocolException(string message)
        : base(message)
    {
    }

    public BlueTuskProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}


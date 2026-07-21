namespace BlueTusk.Security;

/// <summary>Represents an authentication exchange that was rejected or could not be verified.</summary>
public sealed class BlueTuskAuthenticationException : Exception
{
    public BlueTuskAuthenticationException(string message)
        : base(message)
    {
    }

    public BlueTuskAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}


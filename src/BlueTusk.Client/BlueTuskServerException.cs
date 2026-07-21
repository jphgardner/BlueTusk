using BlueTusk.Protocol;

namespace BlueTusk.Client;

/// <summary>An error reported by PostgreSQL.</summary>
public sealed class BlueTuskServerException : Exception
{
    public BlueTuskServerException(BlueTuskError error)
        : base((error ?? throw new ArgumentNullException(nameof(error))).Message)
    {
        Error = error;
    }

    public BlueTuskError Error { get; }

    public string? SqlState => Error.SqlState;

    public string Severity => Error.Severity;
}


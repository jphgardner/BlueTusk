using System.Data.Common;

namespace BlueTusk.Data;

/// <summary>The base exception raised for PostgreSQL and provider errors.</summary>
public class BlueTuskException : DbException
{
    public BlueTuskException()
    {
    }

    public BlueTuskException(string message)
        : base(message)
    {
    }

    public BlueTuskException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal BlueTuskException(BlueTusk.Client.BlueTuskServerException exception)
        : base(exception.Message, exception)
    {
        SqlState = exception.SqlState;
        Severity = exception.Severity;
    }

    public override string? SqlState { get; }

    public string? Severity { get; }
}

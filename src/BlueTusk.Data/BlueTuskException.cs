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
}


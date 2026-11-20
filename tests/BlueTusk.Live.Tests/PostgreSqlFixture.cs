using System.Runtime.CompilerServices;

namespace BlueTusk.Live.Tests;

internal static class PostgreSqlFixture
{
    private const string ConnectionStringVariable = "BLUETUSK_TEST_CONNECTION_STRING";
    internal const string MissingConnectionString = "BLUETUSK_TEST_CONNECTION_STRING is not configured.";

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable));

    public static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        return !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new InvalidOperationException(MissingConnectionString);
    }
}

internal sealed class PostgreSqlFactAttribute : FactAttribute
{
#if NET8_0_OR_GREATER
    public PostgreSqlFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
#else
    public PostgreSqlFactAttribute()
#endif
    {
        if (!PostgreSqlFixture.IsConfigured)
        {
            Skip = PostgreSqlFixture.MissingConnectionString;
        }
    }
}

internal sealed class PostgreSqlTheoryAttribute : TheoryAttribute
{
#if NET8_0_OR_GREATER
    public PostgreSqlTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
#else
    public PostgreSqlTheoryAttribute()
#endif
    {
        if (!PostgreSqlFixture.IsConfigured)
        {
            Skip = PostgreSqlFixture.MissingConnectionString;
        }
    }
}

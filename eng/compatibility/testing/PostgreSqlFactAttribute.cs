using System.Runtime.CompilerServices;
using Xunit;

namespace BlueTusk.Testing.Compatibility;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PostgreSqlFactAttribute : FactAttribute
{
    public const string ConnectionStringVariable = "BLUETUSK_TEST_CONNECTION_STRING";

#if NET8_0_OR_GREATER
    public PostgreSqlFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
#else
    public PostgreSqlFactAttribute()
#endif
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)))
        {
            Skip = "A PostgreSQL test fixture is required.";
        }
    }
}

using System.Runtime.CompilerServices;
using Xunit;

namespace BlueTusk.Testing.Compatibility;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PostgreSqlTheoryAttribute : TheoryAttribute
{
#if NET8_0_OR_GREATER
    public PostgreSqlTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
#else
    public PostgreSqlTheoryAttribute()
#endif
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringVariable)))
        {
            Skip = "A PostgreSQL test fixture is required.";
        }
    }
}

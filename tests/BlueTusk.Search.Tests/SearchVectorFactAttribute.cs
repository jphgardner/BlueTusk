using System.Runtime.CompilerServices;

namespace BlueTusk.Search.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class SearchVectorFactAttribute : FactAttribute
{
#if NET8_0_OR_GREATER
    public SearchVectorFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
#else
    public SearchVectorFactAttribute()
#endif
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING")))
        {
            Skip = "A PostgreSQL test fixture with pgvector is required.";
        }
    }
}

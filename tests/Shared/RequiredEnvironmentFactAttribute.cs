using System.Runtime.CompilerServices;
using Xunit;

namespace BlueTusk.Tests.Shared;

internal sealed class RequiredEnvironmentFactAttribute : FactAttribute
{
#if NET8_0_OR_GREATER
    public RequiredEnvironmentFactAttribute(string variable, [CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
#else
    public RequiredEnvironmentFactAttribute(string variable)
#endif
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {
            Skip = $"{variable} is not configured.";
        }
    }
}

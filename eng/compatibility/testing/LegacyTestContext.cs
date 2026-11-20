#if !NET8_0_OR_GREATER
global using TestContext = BlueTusk.Testing.Compatibility.LegacyTestContext;

namespace BlueTusk.Testing.Compatibility;

// xUnit 2 has no ambient test cancellation token. Individual operation timeouts
// still bound legacy tests; modern suites retain xUnit's real TestContext.
internal sealed class LegacyTestContext
{
    public static LegacyTestContext Current { get; } = new();

    public CancellationToken CancellationToken { get; }
}
#endif

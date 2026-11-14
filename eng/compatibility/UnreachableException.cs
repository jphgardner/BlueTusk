#if !NET7_0_OR_GREATER
namespace BlueTusk.Compatibility;

internal sealed class UnreachableException : Exception
{
    public UnreachableException()
        : base("The program executed an instruction that was thought to be unreachable.")
    {
    }
}
#endif

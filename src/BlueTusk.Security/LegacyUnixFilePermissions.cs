#if !NET7_0_OR_GREATER
using System.Reflection;

namespace BlueTusk.Security;

internal static class LegacyUnixFilePermissions
{
    public static bool HasSafePasswordFilePermissions(string path)
    {
        // Use the frozen .NET 5/6 runtime's own portable stat wrapper, avoiding
        // platform-specific libc struct layouts on Linux and macOS.
        var systemType = typeof(File).Assembly.GetType("Interop+Sys", throwOnError: true)!;
        var stat = systemType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "Stat" && method.GetParameters() is var parameters
                && parameters.Length == 2 && parameters[0].ParameterType == typeof(string));
        object?[] arguments = [path, null];
        if ((int)stat.Invoke(null, arguments)! != 0)
        {
            return false;
        }

        var mode = (int)arguments[1]!.GetType()
            .GetField("Mode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(arguments[1])!;
        // POSIX mode bits 00077: no access for group or other users.
        return (mode & 0x3F) == 0;
    }
}
#endif

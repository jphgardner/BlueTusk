using System.Runtime.InteropServices;

namespace BlueTusk.Testing.Compatibility;

internal static class FilePermissionCompatibility
{
    public static void SetPasswordFilePermissions(string path, bool groupReadable = false)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

#if NET7_0_OR_GREATER
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | (groupReadable ? UnixFileMode.GroupRead : 0));
#else
        if (ChMod(path, groupReadable ? 0x1A0 : 0x180) != 0)
        {
            throw new IOException("Setting password-file permissions failed.");
        }
#endif
    }

#if !NET7_0_OR_GREATER
    [DllImport("System.Native", EntryPoint = "SystemNative_ChMod", SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int ChMod([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
#endif
}

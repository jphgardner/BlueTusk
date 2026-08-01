using System.Globalization;

namespace BlueTusk.Security;

/// <summary>Resolves credentials from PostgreSQL password files.</summary>
public static class BlueTuskPasswordFile
{
    private const UnixFileMode DisallowedUnixPermissions =
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;

    /// <summary>Gets the platform-default PostgreSQL password-file path.</summary>
    public static string? GetDefaultPath()
    {
        var configured = Environment.GetEnvironmentVariable("PGPASSFILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (OperatingSystem.IsWindows())
        {
            var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrWhiteSpace(applicationData)
                ? null
                : Path.Combine(applicationData, "postgresql", "pgpass.conf");
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".pgpass");
    }

    /// <summary>Returns the first password-file entry matching the connection parameters.</summary>
    public static string? Resolve(
        string path,
        string host,
        int port,
        string database,
        string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65_535);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(username);

        if (!File.Exists(path) || !HasSafePermissions(path))
        {
            return null;
        }

        return FindMatch(
            File.ReadLines(path),
            host,
            port.ToString(CultureInfo.InvariantCulture),
            database,
            username);
    }

    /// <summary>Asynchronously returns the first matching password-file entry.</summary>
    public static async ValueTask<string?> ResolveAsync(
        string path,
        string host,
        int port,
        string database,
        string username,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65_535);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(username);

        if (!File.Exists(path) || !HasSafePermissions(path))
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        return FindMatch(
            lines,
            host,
            port.ToString(CultureInfo.InvariantCulture),
            database,
            username);
    }

    private static string? FindMatch(
        IEnumerable<string> lines,
        string host,
        string port,
        string database,
        string username)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = ParseLine(line);
            if (fields is null
                || !Matches(fields[0], host)
                || !Matches(fields[1], port)
                || !Matches(fields[2], database)
                || !Matches(fields[3], username))
            {
                continue;
            }

            return fields[4];
        }

        return null;
    }

    private static bool HasSafePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return (File.GetUnixFileMode(path) & DisallowedUnixPermissions) == 0;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static string[]? ParseLine(string line)
    {
        var fields = new List<string>(5);
        var field = new System.Text.StringBuilder();
        var escaped = false;
        foreach (var character in line)
        {
            if (escaped)
            {
                field.Append(character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == ':')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        if (escaped)
        {
            field.Append('\\');
        }

        fields.Add(field.ToString());
        return fields.Count == 5 ? fields.ToArray() : null;
    }

    private static bool Matches(string pattern, string value) =>
        pattern == "*" || string.Equals(pattern, value, StringComparison.Ordinal);
}

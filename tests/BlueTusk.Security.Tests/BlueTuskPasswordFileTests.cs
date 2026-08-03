namespace BlueTusk.Security.Tests;

public sealed class BlueTuskPasswordFileTests
{
    [Fact]
    public void Resolves_the_first_exact_or_wildcard_match_and_unescapes_fields()
    {
        var path = CreatePasswordFile(
            """
            # specific entries must precede wildcard entries
            db\:primary:5544:app\:service:user\:name:exact\:secret
            *:*:*:*:fallback
            """);

        try
        {
            Assert.Equal(
                "exact:secret",
                BlueTuskPasswordFile.Resolve(path, "db:primary", 5544, "app:service", "user:name"));
            Assert.Equal(
                "fallback",
                BlueTuskPasswordFile.Resolve(path, "other", 5432, "app", "other"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Resolves_matching_entries_asynchronously()
    {
        var path = CreatePasswordFile("host:5432:app:user:secret");
        try
        {
            Assert.Equal(
                "secret",
                await BlueTuskPasswordFile.ResolveAsync(path, "host", 5432, "app", "user"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ignores_malformed_and_nonmatching_entries()
    {
        var path = CreatePasswordFile(
            """
            malformed
            host:5432:other:user:wrong
            """);

        try
        {
            Assert.Null(BlueTuskPasswordFile.Resolve(path, "host", 5432, "app", "user"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ignores_a_missing_password_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bluetusk-{Guid.NewGuid():N}.pgpass");

        Assert.Null(BlueTuskPasswordFile.Resolve(path, "host", 5432, "app", "user"));
    }

    [Fact]
    public void Ignores_group_or_world_accessible_files_on_Unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = CreatePasswordFile("host:5432:app:user:secret");
        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

            Assert.Null(BlueTuskPasswordFile.Resolve(path, "host", 5432, "app", "user"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreatePasswordFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bluetusk-{Guid.NewGuid():N}.pgpass");
        File.WriteAllText(path, contents);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }
}

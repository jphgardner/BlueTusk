using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlueTusk.Live.Tests;

public sealed class LiveApiFreezeTests
{
    [Fact]
    public void Every_live_public_api_baseline_matches_the_candidate_freeze()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(repositoryRoot, "eng", "live-api-freeze.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("1.1.0-candidate", root.GetProperty("baseline").GetString());
        Assert.Equal("utf8-lf", root.GetProperty("normalization").GetString());

        var registered = root.GetProperty("files")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("path").GetString()!,
                item => item.GetProperty("sha256").GetString()!,
                StringComparer.Ordinal);
        var discovered = Directory
            .EnumerateFiles(
                Path.Combine(repositoryRoot, "src"),
                "PublicAPI.Unshipped.txt",
                SearchOption.AllDirectories)
            .Where(path => Path
                .GetFileName(Path.GetDirectoryName(path)!)
                .StartsWith("BlueTusk.Live", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(discovered, registered.Keys.Order(StringComparer.Ordinal));
        foreach (var path in discovered)
        {
            var contents = File
                .ReadAllText(Path.Combine(repositoryRoot, path))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            var digest = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(contents)))
                .ToLowerInvariant();
            Assert.Equal(registered[path], digest);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlueTusk.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the BlueTusk repository root.");
    }
}

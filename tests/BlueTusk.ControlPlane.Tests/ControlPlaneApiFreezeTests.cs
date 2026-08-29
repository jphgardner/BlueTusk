using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace BlueTusk.ControlPlane.Tests;

public sealed class ControlPlaneApiFreezeTests
{
    [Fact]
    public void Every_control_plane_public_api_baseline_matches_the_v1_2_development_freeze()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(
            repositoryRoot,
            "eng",
            "control-plane-api-freeze.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("1.2.0-development", root.GetProperty("baseline").GetString());
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
                "PublicAPI.*.txt",
                SearchOption.AllDirectories)
            .Where(IsControlPlaneApiBaseline)
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(discovered, registered.Keys.Order(StringComparer.Ordinal));
        foreach (var path in discovered)
        {
            var shippedPath = Path.Combine(repositoryRoot, path);
            var contents = Normalize(File.ReadAllText(shippedPath));
            var digest = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(contents)))
                .ToLowerInvariant();
            Assert.Equal(registered[path], digest);
        }
    }

    private static bool IsControlPlaneApiBaseline(string path)
    {
        var project = Directory
            .EnumerateFiles(Path.GetDirectoryName(path)!, "*.csproj", SearchOption.TopDirectoryOnly)
            .Single();
        var document = XDocument.Load(project);
        var declaredFamily = document.Descendants("BlueTuskProductFamily")
            .Select(element => element.Value)
            .FirstOrDefault();

        return string.Equals(declaredFamily, "ControlPlane", StringComparison.Ordinal);
    }

    private static string Normalize(string contents) =>
        contents
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n') + "\n";

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

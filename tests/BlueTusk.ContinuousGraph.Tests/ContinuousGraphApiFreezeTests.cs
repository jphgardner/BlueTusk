using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace BlueTusk.ContinuousGraph.Tests;

public sealed class ContinuousGraphApiFreezeTests
{
    [Fact]
    public void Every_continuous_graph_public_api_baseline_matches_the_v1_1_candidate_freeze()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(
            repositoryRoot,
            "eng",
            "continuous-graph-api-freeze.json");
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
                "PublicAPI.*.txt",
                SearchOption.AllDirectories)
            .Where(IsContinuousGraphApiBaseline)
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(discovered, registered.Keys.Order(StringComparer.Ordinal));
        foreach (var path in discovered)
        {
            var baselinePath = Path.Combine(repositoryRoot, path);
            var contents = Normalize(File.ReadAllText(baselinePath));
            var digest = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(contents)))
                .ToLowerInvariant();
            Assert.Equal(registered[path], digest);
        }
    }

    private static bool IsContinuousGraphApiBaseline(string path)
    {
        var project = Directory
            .EnumerateFiles(
                Path.GetDirectoryName(path)!,
                "*.csproj",
                SearchOption.TopDirectoryOnly)
            .Single();
        var document = XDocument.Load(project);
        var declaredFamily = document.Descendants("BlueTuskProductFamily")
            .Select(element => element.Value)
            .FirstOrDefault();

        return string.Equals(
            declaredFamily,
            "ContinuousGraph",
            StringComparison.Ordinal);
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

        throw new DirectoryNotFoundException(
            "Could not locate the BlueTusk repository root.");
    }
}

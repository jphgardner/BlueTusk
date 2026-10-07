using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlueTusk.Streams.Tests;

public sealed class StreamsApiFreezeTests
{
    [Fact]
    public void Every_streams_public_api_baseline_matches_the_candidate_freeze()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(repositoryRoot, "eng", "streams-api-freeze.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("1.0.0-candidate", root.GetProperty("baseline").GetString());
        Assert.Equal("utf8-lf", root.GetProperty("normalization").GetString());

        var registered = root.GetProperty("files")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("path").GetString()!,
                item => item.GetProperty("sha256").GetString()!,
                StringComparer.Ordinal);
        var discovered = Directory
            .EnumerateFiles(Path.Combine(repositoryRoot, "src"), "PublicAPI.Unshipped.txt", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(Path.GetDirectoryName(path)!).StartsWith("BlueTusk.Streams", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // A reviewed 1.1.0 addition records the new file digest and its exact added signatures.
        // Removing those signatures must reproduce the frozen candidate digest, so an addition
        // can never change or remove part of the candidate surface.
        var reviewedAdditions = root.GetProperty("reviewedAdditions")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("path").GetString()!,
                item => new
                {
                    Release = item.GetProperty("release").GetString()!,
                    Digest = item.GetProperty("sha256").GetString()!,
                    Signatures = item.GetProperty("signatures")
                        .EnumerateArray()
                        .Select(signature => signature.GetString()!)
                        .ToArray(),
                },
                StringComparer.Ordinal);

        Assert.Equal(discovered, registered.Keys.Order(StringComparer.Ordinal));
        Assert.All(reviewedAdditions.Keys, path => Assert.Contains(path, discovered));
        foreach (var path in discovered)
        {
            var contents = File.ReadAllText(Path.Combine(repositoryRoot, path)).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!reviewedAdditions.TryGetValue(path, out var addition))
            {
                Assert.Equal(registered[path], Digest(contents));
                continue;
            }

            Assert.Equal("1.1.0", addition.Release);
            Assert.Equal(addition.Digest, Digest(contents));
            Assert.NotEmpty(addition.Signatures);
            var lines = contents.Split('\n').ToList();
            foreach (var signature in addition.Signatures)
            {
                Assert.Single(lines, line => string.Equals(line, signature, StringComparison.Ordinal));
                _ = lines.Remove(signature);
            }

            Assert.Equal(registered[path], Digest(string.Join('\n', lines)));
        }
    }

    private static string Digest(string contents) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents))).ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlueTusk.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the BlueTusk repository root.");
    }
}

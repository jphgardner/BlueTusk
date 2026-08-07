using System.Text.Json;
using BlueTusk.Sync.Nats;
using BlueTusk.Sync.OpenSearch;
using BlueTusk.Sync.PostgreSql;
using BlueTusk.Sync.Redis;

namespace BlueTusk.Sync.Tests;

public sealed class SyncFormatCompatibilityTests
{
    [Fact]
    public void Registry_matches_implementation_and_named_evidence()
    {
        var repositoryRoot = FindRepositoryRoot();
        var registryPath = Path.Combine(repositoryRoot, "eng", "sync-formats.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(registryPath));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var registered = root.GetProperty("formats")
            .EnumerateArray()
            .Select(ReadFormat)
            .ToArray();

        Assert.Equal(registered.Length, registered.Select(format => format.Id).Distinct().Count());
        var expected = new Dictionary<string, (int Current, int Minimum, string Compatibility)>(
            StringComparer.Ordinal)
        {
            ["nats-envelope"] = (
                NatsSyncEnvelopeReader.CurrentFormatVersion,
                NatsSyncEnvelopeReader.MinimumSupportedFormatVersion,
                "current-only"),
            ["opensearch-storage"] = (
                OpenSearchSyncDestination.CurrentFormatVersion,
                OpenSearchSyncDestination.MinimumSupportedFormatVersion,
                "current-only"),
            ["postgresql-sync-schema"] = (
                PostgreSqlSyncDestination.CurrentSchemaVersion,
                PostgreSqlSyncDestination.MinimumSupportedSchemaVersion,
                "migrated-in-place"),
            ["redis-document"] = (
                RedisSyncDocumentReader.CurrentFormatVersion,
                RedisSyncDocumentReader.MinimumSupportedFormatVersion,
                "current-only"),
            ["redis-storage"] = (
                RedisSyncDestination.CurrentStorageFormatVersion,
                RedisSyncDestination.MinimumSupportedStorageFormatVersion,
                "current-only"),
            ["transform-fingerprint"] = (
                SyncTransformVersion.CurrentFingerprintFormatVersion,
                SyncTransformVersion.MinimumSupportedFingerprintFormatVersion,
                "stable-fingerprint"),
        };

        Assert.Equal(expected.Keys.Order(), registered.Select(format => format.Id).Order());
        foreach (var format in registered)
        {
            var contract = expected[format.Id];
            Assert.Equal(contract.Current, format.CurrentVersion);
            Assert.Equal(contract.Minimum, format.MinimumReadableVersion);
            Assert.Equal(contract.Compatibility, format.Compatibility);
            Assert.InRange(format.MinimumReadableVersion, 1, format.CurrentVersion);
            AssertEvidenceExists(repositoryRoot, format.Evidence);
        }
    }

    private static RegisteredFormat ReadFormat(JsonElement element) => new(
        element.GetProperty("id").GetString()!,
        element.GetProperty("currentVersion").GetInt32(),
        element.GetProperty("minimumReadableVersion").GetInt32(),
        element.GetProperty("compatibility").GetString()!,
        element.GetProperty("evidence").GetString()!);

    private static void AssertEvidenceExists(string repositoryRoot, string evidence)
    {
        var parts = evidence.Split("::", 2, StringSplitOptions.TrimEntries);
        Assert.Equal(2, parts.Length);
        var path = Path.Combine(
            repositoryRoot,
            parts[0].Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Format evidence file does not exist: {parts[0]}");
        Assert.Contains(parts[1], File.ReadAllText(path), StringComparison.Ordinal);
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

    private sealed record RegisteredFormat(
        string Id,
        int CurrentVersion,
        int MinimumReadableVersion,
        string Compatibility,
        string Evidence);
}

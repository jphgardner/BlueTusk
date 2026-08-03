using System.Text.Json;

namespace BlueTusk.ControlPlane.Tests;

public sealed class ControlPlaneFormatCompatibilityTests
{
    [Fact]
    public void Registry_matches_implementation_and_named_evidence()
    {
        var repositoryRoot = FindRepositoryRoot();
        var registryPath = Path.Combine(
            repositoryRoot,
            "eng",
            "control-plane-formats.json");
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
            ["agent-api-contract"] = (
                ControlPlaneApiContract.CurrentVersion,
                ControlPlaneApiContract.MinimumSupportedVersion,
                "version-negotiated"),
            ["postgresql-audit-record"] = (
                PostgreSqlControlPlaneAuditStore.CurrentRecordFormatVersion,
                PostgreSqlControlPlaneAuditStore.MinimumSupportedRecordFormatVersion,
                "current-only"),
            ["postgresql-audit-schema"] = (
                PostgreSqlControlPlaneAuditStore.CurrentSchemaVersion,
                PostgreSqlControlPlaneAuditStore.MinimumSupportedSchemaVersion,
                "migrated-in-place"),
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

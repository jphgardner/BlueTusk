using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlueTusk.Tool;

internal static partial class BlueTuskDoctor
{
    private static async Task CheckCandidateAsync(
        string manifestPath,
        string? assetsPath,
        List<DoctorCheck> checks,
        CancellationToken cancellationToken)
    {
        var (version, packages) = await CheckManifestAsync(manifestPath, checks, cancellationToken)
            .ConfigureAwait(false);
        if (version is null || assetsPath is null)
        {
            return;
        }

        try
        {
            using var assets = await ReadJsonAsync(assetsPath, cancellationToken).ConfigureAwait(false);
            var found = false;
            var failed = false;
            foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
            {
                var separator = library.Name.IndexOf('/');
                if (separator <= 0 || !IsBlueTuskPackage(library.Name[..separator]))
                {
                    continue;
                }

                found = true;
                var id = library.Name[..separator];
                if (library.Value.GetProperty("type").GetString() != "package")
                {
                    checks.Add(new DoctorCheck("project-assets", DoctorCheckStatus.Fail,
                        "A BlueTusk dependency resolved from a project. Restore against the candidate feed using PackageReference."));
                    failed = true;
                }
                else if (!packages.TryGetValue(id, out var packageHash))
                {
                    checks.Add(new DoctorCheck("project-assets", DoctorCheckStatus.Fail,
                        "A restored BlueTusk package is missing from the manifest. Select the candidate manifest covering this application's package set."));
                    failed = true;
                }
                else if (!library.Name.AsSpan(separator + 1).Equals(version.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    checks.Add(new DoctorCheck("project-assets", DoctorCheckStatus.Fail,
                        "A restored BlueTusk version differs from the candidate. Pin the candidate version and restore again."));
                    failed = true;
                }
                else if (!library.Value.TryGetProperty("sha512", out var restoredHash) ||
                    !string.Equals(restoredHash.GetString(), packageHash, StringComparison.Ordinal))
                {
                    checks.Add(new DoctorCheck("project-assets", DoctorCheckStatus.Fail,
                        $"Restored package '{id}' has a missing or different NuGet content hash. Restore from the verified candidate feed with a fresh packages directory."));
                    failed = true;
                }
            }

            if (!found)
            {
                checks.Add(new DoctorCheck("project-assets", DoctorCheckStatus.Fail,
                    "No BlueTusk dependencies were resolved. Supply the application project's assets file after restore."));
            }
            else if (!failed)
            {
                checks.Add(new DoctorCheck("project-assets", DoctorCheckStatus.Pass,
                    "All restored BlueTusk dependencies match the package IDs, versions, and content hashes in the candidate feed."));
            }
        }
        catch (OperationCanceledException)
        {
            checks.Add(new DoctorCheck("project-assets", DoctorCheckStatus.Fail,
                "The diagnostic timed out while reading restored assets. Check local storage or increase --timeout."));
        }
        catch (Exception exception) when (IsCandidateFileError(exception))
        {
            checks.Add(new DoctorCheck("project-assets", DoctorCheckStatus.Fail,
                "Could not read the restored package graph. Restore the project and supply its project.assets.json file."));
        }
    }

    private static async Task<(string? Version, Dictionary<string, string> Packages)> CheckManifestAsync(
        string manifestPath,
        List<DoctorCheck> checks,
        CancellationToken cancellationToken)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var manifest = await ReadJsonAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var root = manifest.RootElement;
            var version = root.GetProperty("packageVersion").GetString();
            var commit = root.GetProperty("sourceCommit").GetString();
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                version is null || !CandidateVersion().IsMatch(version) ||
                commit is null || commit.Length != 40 || !commit.All(Uri.IsHexDigit))
            {
                throw new FormatException();
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            var archives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var suffix = $".{version}.nupkg";
            var failures = 0;
            foreach (var archive in root.GetProperty("archives").EnumerateArray())
            {
                var relativePath = archive.GetProperty("path").GetString();
                var expected = archive.GetProperty("sha256").GetString();
                if (relativePath is null || !CandidateArchivePath().IsMatch(relativePath) ||
                    !archives.Add(relativePath) || expected is null || expected.Length != 64 ||
                    !expected.All(Uri.IsHexDigit))
                {
                    throw new FormatException();
                }

                var fromFeed = relativePath.StartsWith("feed/", StringComparison.OrdinalIgnoreCase);
                if (fromFeed && (!relativePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    relativePath.Length <= 5 + suffix.Length))
                {
                    throw new FormatException();
                }

                var path = Path.Combine(directory, relativePath);
                var (matches, packageHash) = await CheckArchiveAsync(path, expected, fromFeed, cancellationToken)
                    .ConfigureAwait(false);
                if (!matches)
                {
                    checks.Add(new DoctorCheck("package-manifest", DoctorCheckStatus.Fail,
                        $"Archive '{relativePath}' is missing, linked, or has the wrong SHA-256. Replace it from the verified candidate."));
                    failures++;
                    continue;
                }

                if (fromFeed && !packages.TryAdd(relativePath[5..^suffix.Length], packageHash!))
                {
                    throw new FormatException();
                }
            }

            if (failures != 0)
            {
                return (null, packages);
            }

            if (packages.Count == 0 ||
                root.TryGetProperty("nugetCount", out var count) && count.GetInt32() != packages.Count)
            {
                throw new FormatException();
            }

            foreach (var path in Directory.EnumerateFiles(Path.Combine(directory, "feed"), "*.nupkg"))
            {
                if (!archives.Contains($"feed/{Path.GetFileName(path)}"))
                {
                    checks.Add(new DoctorCheck("package-manifest", DoctorCheckStatus.Fail,
                        "The feed contains an unlisted archive. Use an isolated copy of the candidate feed."));
                    return (null, packages);
                }
            }

            checks.Add(new DoctorCheck("package-manifest", DoctorCheckStatus.Pass,
                $"Verified SHA-256 for {archives.Count} listed candidate archives."));
            if (root.TryGetProperty("omittedPackages", out var omissions) &&
                omissions.ValueKind == JsonValueKind.Array && omissions.GetArrayLength() > 0)
            {
                checks.Add(new DoctorCheck("package-manifest", DoctorCheckStatus.Warning,
                    "The manifest records omitted packages. Review the candidate's scope before deployment."));
            }

            return (version, packages);
        }
        catch (OperationCanceledException)
        {
            checks.Add(new DoctorCheck("package-manifest", DoctorCheckStatus.Fail,
                "Candidate verification timed out. Check local storage or increase --timeout."));
        }
        catch (Exception exception) when (IsCandidateFileError(exception))
        {
            checks.Add(new DoctorCheck("package-manifest", DoctorCheckStatus.Fail,
                "Could not verify the candidate manifest. Supply schema 1 sha256.json in the root of the extracted candidate."));
        }

        return (null, packages);
    }

    private static async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(bool Matches, string? PackageHash)> CheckArchiveAsync(
        string path,
        string expected,
        bool fromFeed,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) ||
            (File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return (false, null);
        }

        await using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null);
        }

        if (!fromFeed)
        {
            return (true, null);
        }

        stream.Position = 0;
        var packageHash = await SHA512.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return (true, Convert.ToBase64String(packageHash));
    }

    private static bool IsBlueTuskPackage(string id) =>
        id.Equals("BlueTusk", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("BlueTusk.", StringComparison.OrdinalIgnoreCase);

    private static bool IsCandidateFileError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or FormatException or
            ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException;

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex CandidateVersion();

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9._-]*\.(?:nupkg|snupkg|tgz)\z", RegexOptions.CultureInvariant)]
    private static partial Regex CandidateArchivePath();
}

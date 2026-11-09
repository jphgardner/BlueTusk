using System.Security.Cryptography;
using System.Text.Json;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Tool;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class BlueTuskCliTests
{
    [Fact]
    public void Help_and_usage_errors_do_not_require_a_database()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, BlueTuskCli.Run(["--help"], output, error));
        Assert.Contains("bluetusk scaffold", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("bluetusk doctor", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, BlueTuskCli.Run(["doctor", "--help"], output, error));
        Assert.Contains("--require-streams", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("read-only", output.ToString(), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        Assert.Equal(2, BlueTuskCli.Run(["scaffold", "--unknown"], output, error));
        Assert.Contains("Unknown scaffold option", error.ToString(), StringComparison.Ordinal);

        error.GetStringBuilder().Clear();
        Assert.Equal(2, BlueTuskCli.Run(["doctor", "--unknown"], output, error));
        Assert.Contains("Unknown doctor option", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_requires_a_bounded_timeout()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(2, BlueTuskCli.Run(
            ["doctor", "--connection", "Host=localhost", "--timeout", "0"],
            output,
            error));
        Assert.Contains("1 to 120", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("package", "1.1.0-rc.2.local.1", false, false, 0, "All restored BlueTusk dependencies")]
    [InlineData("package", "1.1.0-rc.1", false, false, 1, "Pin the candidate version")]
    [InlineData("project", "1.1.0-rc.2.local.1", false, false, 1, "Restore against the candidate feed")]
    [InlineData("package", "1.1.0-rc.2.local.1", true, false, 1, "wrong SHA-256")]
    [InlineData("package", "1.1.0-rc.2.local.1", false, true, 1, "different NuGet content hash")]
    public void Doctor_checks_the_local_manifest_and_restored_project(
        string dependencyType,
        string resolvedVersion,
        bool changeArchive,
        bool changeRestoredHash,
        int expectedExitCode,
        string expectedMessage)
    {
        const string candidateVersion = "1.1.0-rc.2.local.1";
        var directory = Directory.CreateTempSubdirectory("bluetusk-doctor-");
        try
        {
            var archiveName = $"BlueTusk.Data.{candidateVersion}.nupkg";
            var feed = directory.CreateSubdirectory("feed");
            var archivePath = Path.Combine(feed.FullName, archiveName);
            byte[] content = [1, 2, 3];
            File.WriteAllBytes(archivePath, content);
            var manifest = Path.Combine(directory.FullName, "sha256.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                sourceCommit = new string('a', 40),
                packageVersion = candidateVersion,
                archives = new[]
                {
                    new { path = $"feed/{archiveName}", sha256 = Convert.ToHexString(SHA256.HashData(content)) },
                },
            }));
            if (changeArchive)
            {
                File.WriteAllBytes(archivePath, [1, 2, 4]);
            }

            var assets = Path.Combine(directory.FullName, "project.assets.json");
            byte[] restoredContent = changeRestoredHash ? [1, 2, 4] : content;
            var sha512 = Convert.ToBase64String(SHA512.HashData(restoredContent));
            File.WriteAllText(assets, JsonSerializer.Serialize(new
            {
                libraries = new Dictionary<string, object>
                {
                    [$"BlueTusk.Data/{resolvedVersion}"] = new { type = dependencyType, sha512 },
                },
            }));
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = BlueTuskCli.Run(
                ["doctor", "--local-only", "--package-manifest", manifest, "--project-assets", assets, "--json"],
                output,
                error);

            Assert.Equal(expectedExitCode, exitCode);
            Assert.Contains(expectedMessage, output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Doctor_does_not_print_credential_fragments_from_connection_errors()
    {
        const string secret = "credential-fragment-123";
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = BlueTuskCli.Run(
            ["doctor", "--connection", $"Host=localhost;{secret}=value", "--json"],
            output,
            error);

        Assert.Equal(1, result);
        Assert.Contains("supported option names", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Doctor_reports_machine_readable_production_state()
    {
        var connectionString = ConnectionString();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = BlueTuskCli.Run(
            ["doctor", "--connection", connectionString, "--json", "--extension", "plpgsql"],
            output,
            error);

        Assert.Equal(0, result);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("\"schemaVersion\": 1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"status\": \"ready\"", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_reports_the_server_major_and_minor_version()
    {
        var connectionString = ConnectionString();
        int versionNumber;
        await using (var connection = new BlueTuskConnection(connectionString))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using var command = new BlueTuskCommand(
                "SELECT current_setting('server_version_num')::int4",
                connection);
            versionNumber = await command.ExecuteScalarAsync<int>(CancellationToken.None);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = BlueTuskCli.Run(["doctor", "--connection", connectionString], output, error);

        // PostgreSQL 10+ reports major * 10000 + minor, for example 180006 for 18.6.
        var expected = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"PostgreSQL {versionNumber / 10000}.{versionNumber % 10000};");
        Assert.Equal(0, result);
        Assert.Contains(expected, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scaffold_command_writes_filtered_secure_by_default_models()
    {
        var connectionString = ConnectionString();
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"bluetusk-cli-{Guid.NewGuid():N}");
        await Execute(connectionString, "DROP SCHEMA IF EXISTS cli_scaffold CASCADE");
        try
        {
            await Execute(connectionString, """
                CREATE SCHEMA cli_scaffold;
                CREATE TABLE cli_scaffold.people (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    display_name text NOT NULL
                );
                COMMENT ON TABLE cli_scaffold.people IS 'CLI people';
                """);

            using var output = new StringWriter();
            using var error = new StringWriter();
            string[] scaffoldArguments =
            [
                "scaffold",
                "--connection", connectionString,
                "--schema", "cli_scaffold",
                "--output", outputDirectory,
                "--context", "CliContext",
                "--namespace", "CliModels",
                "--include-graphs",
                "--include-functions",
                "--include-views",
            ];
            var result = BlueTuskCli.Run(scaffoldArguments, output, error);

            Assert.Equal(0, result);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Scaffolded 2 file(s)", output.ToString(), StringComparison.Ordinal);
            var files = Directory.GetFiles(outputDirectory, "*.cs");
            Assert.Equal(2, files.Length);
            var contextCode = await File.ReadAllTextAsync(
                Assert.Single(files, file => Path.GetFileName(file) == "CliContext.cs"));
            Assert.Contains("class CliContext", contextCode, StringComparison.Ordinal);
            Assert.Contains("HasComment(\"CLI people\")", contextCode, StringComparison.Ordinal);
            Assert.Contains("UseIdentityColumn(BlueTuskIdentityGeneration.Always)",
                contextCode, StringComparison.Ordinal);
            Assert.DoesNotContain(connectionString, contextCode, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=postgres", contextCode, StringComparison.OrdinalIgnoreCase);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            Assert.Equal(1, BlueTuskCli.Run(scaffoldArguments, output, error));
            Assert.DoesNotContain(connectionString, error.ToString(), StringComparison.Ordinal);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            Assert.Equal(0, BlueTuskCli.Run(
                [.. scaffoldArguments, "--force", "--include-connection-string"],
                output,
                error));
            contextCode = await File.ReadAllTextAsync(
                Assert.Single(files, file => Path.GetFileName(file) == "CliContext.cs"));
            Assert.Contains("UseBlueTusk", contextCode, StringComparison.Ordinal);
            Assert.Contains(connectionString, contextCode, StringComparison.Ordinal);
        }
        finally
        {
            await Execute(connectionString, "DROP SCHEMA IF EXISTS cli_scaffold CASCADE");
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(value)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        }.ConnectionString;
    }
}

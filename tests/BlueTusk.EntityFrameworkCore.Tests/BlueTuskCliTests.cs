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
        Assert.Equal(string.Empty, error.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(2, BlueTuskCli.Run(["scaffold", "--unknown"], output, error));
        Assert.Contains("Unknown scaffold option", error.ToString(), StringComparison.Ordinal);
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

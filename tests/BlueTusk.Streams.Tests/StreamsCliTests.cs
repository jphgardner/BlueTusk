using BlueTusk.Streams.Tool;

namespace BlueTusk.Streams.Tests;

public sealed class StreamsCliTests
{
    [Fact]
    public void Help_describes_validation_and_safe_relay_provisioning()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, BlueTuskStreamsCli.Run(["--help"], output, error));
        Assert.Contains("bluetusk-streams validate", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("bluetusk-streams provision", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, BlueTuskStreamsCli.Run(["provision", "--help"], output, error));
        Assert.Contains("--control-connection", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--direct-only", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--allow-shared-control", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Provision_requires_an_explicit_relay_or_direct_mode()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var previousSource = Environment.GetEnvironmentVariable("BLUETUSK_STREAMS_SOURCE");
        var previousControl = Environment.GetEnvironmentVariable("BLUETUSK_STREAMS_CONTROL");
        try
        {
            Environment.SetEnvironmentVariable("BLUETUSK_STREAMS_SOURCE", null);
            Environment.SetEnvironmentVariable("BLUETUSK_STREAMS_CONTROL", null);
            Assert.Equal(
                2,
                BlueTuskStreamsCli.Run(
                    [
                        "provision",
                        "--connection", "Host=source;Database=app;Username=streams",
                        "--publication", "app_changes",
                        "--slot", "app_streams",
                    ],
                    output,
                    error));
            Assert.Contains("requires a separate relay connection", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLUETUSK_STREAMS_SOURCE", previousSource);
            Environment.SetEnvironmentVariable("BLUETUSK_STREAMS_CONTROL", previousControl);
        }
    }

    [Fact]
    public void Provision_rejects_ambiguous_or_unqualified_table_selection_before_connecting()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] common =
        [
            "provision",
            "--connection", "Host=source;Database=app;Username=streams",
            "--publication", "app_changes",
            "--slot", "app_streams",
            "--direct-only",
        ];

        Assert.Equal(
            2,
            BlueTuskStreamsCli.Run([.. common, "--table", "orders"], output, error));
        Assert.Contains("schema.table", error.ToString(), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        Assert.Equal(
            2,
            BlueTuskStreamsCli.Run(
                [.. common, "--table", "app.orders", "--all-tables"],
                output,
                error));
        Assert.Contains("either --all-tables", error.ToString(), StringComparison.Ordinal);
    }
}

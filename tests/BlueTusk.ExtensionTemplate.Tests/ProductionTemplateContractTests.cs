using System.Text.Json;

namespace BlueTusk.ExtensionTemplate.Tests;

public sealed class ProductionTemplateContractTests
{
    private static readonly string TemplateOutput = Path.Combine(
        AppContext.BaseDirectory,
        "ProductionTemplate");

    [Fact]
    public void Production_template_exposes_exact_version_and_client_choices()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            TemplateOutput,
            "template.json")));
        var root = document.RootElement;

        Assert.Equal("bluetusk-production", root.GetProperty("shortName").GetString());
        Assert.Equal("BlueTusk.OrderOperations", root.GetProperty("sourceName").GetString());

        var symbols = root.GetProperty("symbols");
        var version = symbols.GetProperty("BlueTuskVersion");
        Assert.Equal("1.2.0", version.GetProperty("defaultValue").GetString());
        Assert.Equal("1.2.0-rc.1", version.GetProperty("replaces").GetString());
        var databaseImage = symbols.GetProperty("KubernetesPostgreSqlImage")
            .GetProperty("defaultValue")
            .GetString()!;
        Assert.Contains("postgresql:18-standard-trixie@sha256:", databaseImage, StringComparison.Ordinal);
        Assert.DoesNotContain("beta", databaseImage, StringComparison.OrdinalIgnoreCase);

        var choices = symbols.GetProperty("ClientFramework").GetProperty("choices")
            .EnumerateArray()
            .Select(choice => choice.GetProperty("choice").GetString()!)
            .ToArray();
        Assert.Equal(["react", "angular"], choices);
    }

    [Fact]
    public void Local_stack_is_stable_and_covers_supported_dependencies()
    {
        var compose = File.ReadAllText(Path.Combine(TemplateOutput, "compose.yaml"));

        Assert.Contains("postgres:18-alpine", compose, StringComparison.Ordinal);
        Assert.Contains("redis:", compose, StringComparison.Ordinal);
        Assert.Contains("nats:", compose, StringComparison.Ordinal);
        Assert.Contains("opensearch:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("beta", compose, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Both_browser_clients_pin_the_same_coordinated_release()
    {
        using var react = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            TemplateOutput,
            "react-package.json")));
        using var angular = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            TemplateOutput,
            "angular-package.json")));

        Assert.Equal(
            "1.2.0-rc.1",
            react.RootElement.GetProperty("dependencies").GetProperty("@bluetusk/live").GetString());
        Assert.Equal(
            "1.2.0-rc.1",
            angular.RootElement.GetProperty("dependencies").GetProperty("@bluetusk/live").GetString());
        Assert.True(react.RootElement.GetProperty("dependencies").TryGetProperty(
            "@bluetusk/live-react",
            out _));
        Assert.True(angular.RootElement.GetProperty("dependencies").TryGetProperty(
            "@bluetusk/live-angular",
            out _));
    }

    [Fact]
    public void Generated_solution_keeps_clean_architecture_layers_explicit()
    {
        var solution = File.ReadAllText(Path.Combine(
            TemplateOutput,
            "BlueTusk.ProductionStarter.slnx"));

        Assert.Contains(".Domain/", solution, StringComparison.Ordinal);
        Assert.Contains(".Application/", solution, StringComparison.Ordinal);
        Assert.Contains(".Infrastructure/", solution, StringComparison.Ordinal);
        Assert.Contains(".Api/", solution, StringComparison.Ordinal);
        Assert.Contains(".Worker/", solution, StringComparison.Ordinal);
        Assert.Contains(".Tests/", solution, StringComparison.Ordinal);
    }
}

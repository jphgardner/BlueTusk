using System.Text.Json;
using System.Xml.Linq;

namespace BlueTusk.ConformanceTests;

public sealed class ProductFamilyArchitectureTests
{
    private static readonly IReadOnlyDictionary<string, string> ProductPrefixes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BlueTusk.Streams"] = "Streams",
            ["BlueTusk.Sync"] = "Sync",
            ["BlueTusk.Live"] = "Live",
            ["BlueTusk.ControlPlane"] = "ControlPlane",
            ["BlueTusk.Dashboard"] = "ControlPlane",
            ["BlueTusk.ContinuousGraph"] = "ContinuousGraph",
        };

    [Fact]
    public void Product_families_have_independent_version_manifests()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "product-families.json")));

        foreach (var family in new[] { "Provider", "Streams", "Sync", "Live", "ControlPlane", "ContinuousGraph" })
        {
            var familyElement = manifest.RootElement.GetProperty("families").GetProperty(family);
            var versionFile = familyElement.GetProperty("versionFile").GetString();

            Assert.False(string.IsNullOrWhiteSpace(versionFile));
            Assert.True(
                File.Exists(Path.Combine(repositoryRoot, versionFile.Replace('/', Path.DirectorySeparatorChar))),
                $"The {family} version file '{versionFile}' does not exist.");
        }
    }

    [Fact]
    public void Application_products_cannot_reference_replication_internals()
    {
        foreach (var project in FindProductProjects())
        {
            if (project.Family == "Streams")
            {
                continue;
            }

            Assert.DoesNotContain(
                project.References,
                reference => reference.StartsWith("BlueTusk.Replication", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Product_projects_declare_their_release_family()
    {
        foreach (var project in FindProductProjects())
        {
            Assert.Equal(project.Family, project.DeclaredFamily);
            Assert.Contains(
                project.Imports,
                import => import.Replace('\\', '/').EndsWith(
                    $"eng/versions/{project.Family}.props",
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Streams_core_does_not_take_an_Entity_Framework_dependency()
    {
        foreach (var project in FindProductProjects().Where(project =>
                     project.Family == "Streams" &&
                     !project.Name.Contains("EntityFrameworkCore", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(
                project.References,
                reference => reference.Contains("EntityFrameworkCore", StringComparison.Ordinal));
        }
    }

    private static ProductProject[] FindProductProjects()
    {
        var repositoryRoot = FindRepositoryRoot();
        return Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Select(ReadProductProject)
            .Where(project => project is not null)
            .Cast<ProductProject>()
            .ToArray();
    }

    private static ProductProject? ReadProductProject(string path)
    {
        var document = XDocument.Load(path);
        var name = document.Descendants("AssemblyName").Select(element => element.Value).FirstOrDefault()
            ?? Path.GetFileNameWithoutExtension(path);
        var family = ProductPrefixes
            .Where(entry => name.Equals(entry.Key, StringComparison.Ordinal) ||
                            name.StartsWith(entry.Key + ".", StringComparison.Ordinal))
            .Select(entry => entry.Value)
            .FirstOrDefault();

        if (family is null)
        {
            return null;
        }

        var declaredFamily = document.Descendants("BlueTuskProductFamily")
            .Select(element => element.Value)
            .FirstOrDefault();
        var references = document.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .Select(value => Path.GetFileNameWithoutExtension(value!))
            .ToArray();
        var imports = document.Descendants("Import")
            .Select(element => element.Attribute("Project")?.Value)
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();

        return new ProductProject(name, family, declaredFamily, references, imports);
    }

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

    private sealed record ProductProject(
        string Name,
        string Family,
        string? DeclaredFamily,
        IReadOnlyList<string> References,
        IReadOnlyList<string> Imports);
}

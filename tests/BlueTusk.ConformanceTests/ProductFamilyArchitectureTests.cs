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
    public void Provider_package_manifest_does_not_register_embedded_template_content()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "product-families.json")));

        var projects = FindRegisteredProjects(
            repositoryRoot,
            manifest.RootElement.GetProperty("families").GetProperty("Provider"))
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .ToArray();

        Assert.Contains(
            "templates/BlueTusk.Extension/BlueTusk.Templates.csproj",
            projects);
        Assert.DoesNotContain(
            projects,
            path => path.StartsWith(
                "templates/BlueTusk.Extension/content/",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Provider_library_packages_have_compiler_enforced_api_baselines()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "product-families.json")));

        foreach (var projectPath in FindRegisteredProjects(
                     repositoryRoot,
                     manifest.RootElement.GetProperty("families").GetProperty("Provider")))
        {
            var document = XDocument.Load(projectPath);
            var outputType = document.Descendants("OutputType")
                .Select(element => element.Value)
                .FirstOrDefault();
            var includeBuildOutput = document.Descendants("IncludeBuildOutput")
                .Select(element => element.Value)
                .FirstOrDefault();

            if (string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(includeBuildOutput, "false", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            Assert.True(
                File.Exists(Path.Combine(projectDirectory, "PublicAPI.Shipped.txt")),
                $"{Path.GetRelativePath(repositoryRoot, projectPath)} has no shipped API baseline.");
            Assert.True(
                File.Exists(Path.Combine(projectDirectory, "PublicAPI.Unshipped.txt")),
                $"{Path.GetRelativePath(repositoryRoot, projectPath)} has no unshipped API baseline.");
        }
    }

    [Fact]
    public void Publishable_product_families_have_publishable_release_dependencies()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "product-families.json")));
        var families = manifest.RootElement.GetProperty("families");

        foreach (var family in families.EnumerateObject())
        {
            var dependencies = family.Value.GetProperty("releaseDependencies")
                .EnumerateArray()
                .Select(dependency => dependency.GetString()!)
                .ToArray();

            Assert.Equal(
                dependencies.Length,
                dependencies.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(family.Name, dependencies);
            foreach (var dependency in dependencies)
            {
                Assert.True(
                    families.TryGetProperty(dependency, out var dependencyFamily),
                    $"{family.Name} declares unknown release dependency {dependency}.");
                if (family.Value.GetProperty("publishable").GetBoolean())
                {
                    Assert.True(
                        dependencyFamily.GetProperty("publishable").GetBoolean(),
                        $"{family.Name} cannot be publishable before {dependency}.");
                }
            }
        }
    }

    [Fact]
    public void Cross_family_project_references_are_declared_release_dependencies()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "product-families.json")));
        var families = manifest.RootElement.GetProperty("families");

        foreach (var project in FindProductProjects())
        {
            var dependencies = families.GetProperty(project.Family)
                .GetProperty("releaseDependencies")
                .EnumerateArray()
                .Select(dependency => dependency.GetString()!)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var reference in project.References)
            {
                var referencedFamily = GetProductFamily(reference) ?? "Provider";
                if (!string.Equals(referencedFamily, project.Family, StringComparison.Ordinal))
                {
                    Assert.Contains(referencedFamily, dependencies);
                }
            }
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

    private static string[] FindRegisteredProjects(
        string repositoryRoot,
        JsonElement family)
    {
        return family.GetProperty("packages")
            .EnumerateArray()
            .Select(entry => Path.Combine(
                repositoryRoot,
                entry.GetString()!.Replace('/', Path.DirectorySeparatorChar)))
            .SelectMany(path => Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories)
                : [path])
            .Where(path =>
            {
                var document = XDocument.Load(path);
                var declaredFamily = document.Descendants("BlueTuskProductFamily")
                    .Select(element => element.Value)
                    .FirstOrDefault();

                return string.IsNullOrWhiteSpace(declaredFamily) ||
                       string.Equals(declaredFamily, "Provider", StringComparison.Ordinal);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ProductProject? ReadProductProject(string path)
    {
        var document = XDocument.Load(path);
        var name = document.Descendants("AssemblyName").Select(element => element.Value).FirstOrDefault()
            ?? Path.GetFileNameWithoutExtension(path);
        var family = GetProductFamily(name);

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

    private static string? GetProductFamily(string projectName) =>
        ProductPrefixes
            .Where(entry => projectName.Equals(entry.Key, StringComparison.Ordinal) ||
                            projectName.StartsWith(entry.Key + ".", StringComparison.Ordinal))
            .Select(entry => entry.Value)
            .FirstOrDefault();

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

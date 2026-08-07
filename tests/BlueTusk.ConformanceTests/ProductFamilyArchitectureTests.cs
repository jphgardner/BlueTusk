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
        Assert.Equal(2, manifest.RootElement.GetProperty("schemaVersion").GetInt32());

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
    public void Product_family_package_manifests_list_projects_explicitly()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "product-families.json")));

        foreach (var family in manifest.RootElement
                     .GetProperty("families")
                     .EnumerateObject())
        {
            var packages = family.Value
                .GetProperty("packages")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray();
            Assert.NotEmpty(packages);
            Assert.Equal(
                packages.Length,
                packages.Distinct(StringComparer.Ordinal).Count());
            foreach (var package in packages)
            {
                Assert.EndsWith(".csproj", package, StringComparison.Ordinal);
                Assert.True(
                    File.Exists(Path.Combine(
                        repositoryRoot,
                        package.Replace('/', Path.DirectorySeparatorChar))),
                    $"{family.Name} references missing package project {package}.");
            }
        }
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
    public void Publication_enabled_product_families_have_enabled_release_dependencies()
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
                if (family.Value.GetProperty("publication").GetProperty("enabled").GetBoolean())
                {
                    Assert.True(
                        dependencyFamily
                            .GetProperty("publication")
                            .GetProperty("enabled")
                            .GetBoolean(),
                        $"{family.Name} cannot publish before {dependency}.");
                }
            }
        }
    }

    [Fact]
    public void Publication_policies_require_unique_tags_and_exact_commit_workflows()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "product-families.json")));
        var families = manifest.RootElement.GetProperty("families");
        var tagPrefixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var family in families.EnumerateObject())
        {
            var publication = family.Value.GetProperty("publication");
            var channel = publication.GetProperty("channel").GetString();
            Assert.Equal("stable", channel);

            var tagPrefix = publication.GetProperty("tagPrefix").GetString()!;
            Assert.Matches("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", tagPrefix);
            Assert.True(tagPrefixes.Add(tagPrefix), $"Duplicate release tag prefix: {tagPrefix}");

            var workflowEvidence = publication
                .GetProperty("requiredWorkflowEvidence")
                .EnumerateArray()
                .ToArray();
            Assert.NotEmpty(workflowEvidence);
            Assert.Equal(
                workflowEvidence.Length,
                workflowEvidence
                    .Select(item => item.GetProperty("workflowFile").GetString())
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            foreach (var evidence in workflowEvidence)
            {
                var workflowFile = evidence.GetProperty("workflowFile").GetString()!;
                Assert.True(
                    File.Exists(Path.Combine(
                        repositoryRoot,
                        ".github",
                        "workflows",
                        workflowFile)),
                    $"{family.Name} references missing workflow {workflowFile}.");
                var allowedEvents = evidence
                    .GetProperty("allowedEvents")
                    .EnumerateArray()
                    .Select(item => item.GetString())
                    .ToArray();
                Assert.Single(allowedEvents);
                Assert.Equal("workflow_dispatch", allowedEvents[0]);
            }
        }
    }

    [Fact]
    public void Release_workflow_is_tag_only_fail_closed_and_separates_stable_from_prerelease()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            ".github",
            "workflows",
            "release-product-family.yml"));

        Assert.DoesNotContain("publish:\n        description:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--skip-duplicate", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "needs.verify-and-package.outputs.is_tag == 'true' &&\n" +
            "      needs.verify-and-package.outputs.is_prerelease != 'true'",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "environment: package-production",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "needs.verify-and-package.outputs.is_tag == 'true' &&\n" +
            "      needs.verify-and-package.outputs.is_prerelease == 'true'",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "environment: package-prerelease",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "npm publish \"$package\" --access public --tag rc --provenance",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "./eng/verify-release-gates.ps1",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "./eng/verify-product-family-packages.ps1",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "actions/attest-build-provenance@977bb373ede98d70efdf65b84cb5f73e068dcc2a # v3",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "npm publish \"$package\" --access public --tag \"$tag\" --provenance",
            workflow,
            StringComparison.Ordinal);

        var verifyIndex = workflow.IndexOf(
            "./eng/verify-product-family-packages.ps1",
            StringComparison.Ordinal);
        var uploadIndex = workflow.IndexOf(
            "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4",
            StringComparison.Ordinal);
        Assert.True(
            verifyIndex >= 0 && uploadIndex > verifyIndex,
            "Package contents must be verified before the release artifact is uploaded.");
    }

    [Fact]
    public void Release_gate_verifier_binds_evidence_to_the_exact_commit()
    {
        var repositoryRoot = FindRepositoryRoot();
        var verifier = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "eng",
            "verify-release-gates.ps1"));

        Assert.Contains(
            "[ValidatePattern('^[0-9a-fA-F]{40}$')]",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "head_sha=$Commit",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$_.headSha",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$_.conclusion",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "$allowedEvents -contains [string]$_.event",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "status --porcelain --untracked-files=no",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://api.nuget.org/v3-flatcontainer",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://registry.npmjs.org",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-PublishedResource",
            verifier,
            StringComparison.Ordinal);
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
        var solution = XDocument.Load(Path.Combine(repositoryRoot, "BlueTusk.slnx"));
        return solution.Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(
                repositoryRoot,
                path!.Replace('/', Path.DirectorySeparatorChar)))
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

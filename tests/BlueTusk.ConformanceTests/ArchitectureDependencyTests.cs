using System.Reflection;

namespace BlueTusk.ConformanceTests;

public sealed class ArchitectureDependencyTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedBlueTuskReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["BlueTusk.Transport"] = [],
            ["BlueTusk.Security"] = [],
            ["BlueTusk.Diagnostics"] = [],
            ["BlueTusk.TypeSystem"] = [],
            ["BlueTusk.Protocol"] = ["BlueTusk.Transport"],
            ["BlueTusk.Client"] =
                [
                    "BlueTusk.Diagnostics",
                    "BlueTusk.Protocol",
                    "BlueTusk.Security",
                    "BlueTusk.Transport",
                    "BlueTusk.TypeSystem",
                ],
            ["BlueTusk.Extensions.Abstractions"] = ["BlueTusk.TypeSystem"],
            ["BlueTusk.Extensions.Testing"] =
                ["BlueTusk.Data", "BlueTusk.Extensions.Abstractions", "BlueTusk.TypeSystem"],
            ["BlueTusk.Extensions.Citext"] =
                ["BlueTusk.Data", "BlueTusk.Extensions.Abstractions", "BlueTusk.TypeSystem"],
            ["BlueTusk.Extensions.Citext.EntityFrameworkCore"] =
                ["BlueTusk.Data", "BlueTusk.EntityFrameworkCore", "BlueTusk.Extensions.Citext", "BlueTusk.TypeSystem"],
            ["BlueTusk.Data"] =
                [
                    "BlueTusk.Client",
                    "BlueTusk.Diagnostics",
                    "BlueTusk.Extensions.Abstractions",
                    "BlueTusk.Protocol",
                    "BlueTusk.Security",
                    "BlueTusk.TypeSystem",
                ],
            ["BlueTusk.Replication"] =
                ["BlueTusk.Client", "BlueTusk.Diagnostics", "BlueTusk.Protocol", "BlueTusk.TypeSystem"],
            ["BlueTusk.Replication.PgOutput"] =
                ["BlueTusk.Replication", "BlueTusk.TypeSystem"],
            ["BlueTusk.EntityFrameworkCore"] = ["BlueTusk.Data", "BlueTusk.TypeSystem"],
            ["BlueTusk.EntityFrameworkCore.Design"] =
                ["BlueTusk.Data", "BlueTusk.EntityFrameworkCore"],
        };

    [Fact]
    public void Provider_projects_follow_the_directed_dependency_graph()
    {
        foreach (var (assemblyName, expectedReferences) in AllowedBlueTuskReferences)
        {
            var actualReferences = Assembly.Load(assemblyName)
                .GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(name => name.StartsWith("BlueTusk.", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();

            var forbiddenReferences = actualReferences.Except(expectedReferences, StringComparer.Ordinal).ToArray();
            Assert.True(
                forbiddenReferences.Length == 0,
                $"{assemblyName} has forbidden BlueTusk references: {string.Join(", ", forbiddenReferences)}.");
        }
    }

    [Fact]
    public void Lower_layers_do_not_reference_ADO_NET_or_Entity_Framework_Core()
    {
        var lowerLayers = AllowedBlueTuskReferences.Keys.Where(
            name => name is not (
                "BlueTusk.Data" or
                "BlueTusk.EntityFrameworkCore" or
                "BlueTusk.EntityFrameworkCore.Design" or
                "BlueTusk.Extensions.Citext" or
                "BlueTusk.Extensions.Citext.EntityFrameworkCore" or
                "BlueTusk.Extensions.Testing"));

        foreach (var assemblyName in lowerLayers)
        {
            var references = Assembly.Load(assemblyName).GetReferencedAssemblies();
            Assert.DoesNotContain(
                references,
                reference => string.Equals(reference.Name, "System.Data.Common", StringComparison.Ordinal));
            Assert.DoesNotContain(
                references,
                reference => reference.Name?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true);
        }
    }
}

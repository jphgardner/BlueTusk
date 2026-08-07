using System.Reflection;
using BlueTusk.FraudInvestigation.Application;
using BlueTusk.FraudInvestigation.Domain;
using BlueTusk.OrderOperations.Application;
using BlueTusk.OrderOperations.Domain;
using BlueTusk.ServiceTopology.Application;
using BlueTusk.ServiceTopology.Domain;
using Xunit;

namespace BlueTusk.Applications.ArchitectureTests;

public sealed class DependencyDirectionTests
{
    public static TheoryData<Assembly> DomainAssemblies => new()
    {
        typeof(Account).Assembly,
        typeof(FulfilmentOrder).Assembly,
        typeof(ServiceNode).Assembly,
    };

    public static TheoryData<Assembly> ApplicationAssemblies => new()
    {
        typeof(FraudService).Assembly,
        typeof(OrderService).Assembly,
        typeof(TopologyService).Assembly,
    };

    [Theory]
    [MemberData(nameof(DomainAssemblies))]
    public void Domain_has_no_application_or_infrastructure_dependency(Assembly assembly)
    {
        var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.DoesNotContain(references, name => name?.Contains("Application", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(references, name => name?.Contains("Infrastructure", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(references, IsProductRuntimeReference);
    }

    [Theory]
    [MemberData(nameof(ApplicationAssemblies))]
    public void Application_has_no_infrastructure_dependency(Assembly assembly)
    {
        var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.DoesNotContain(references, name => name?.Contains("Infrastructure", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(references, IsProductRuntimeReference);
    }

    private static bool IsProductRuntimeReference(string? name) =>
        name?.StartsWith("BlueTusk.", StringComparison.Ordinal) is true &&
        !name.EndsWith(".Domain", StringComparison.Ordinal);
}

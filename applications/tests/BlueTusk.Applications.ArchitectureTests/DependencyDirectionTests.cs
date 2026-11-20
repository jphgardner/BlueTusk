using System.Reflection;
using BlueTusk.OrderOperations.Application;
using BlueTusk.OrderOperations.Domain;
#if !BLUETUSK_CORE_PACKAGES_ONLY
using BlueTusk.FraudInvestigation.Application;
using BlueTusk.FraudInvestigation.Domain;
using BlueTusk.ServiceTopology.Application;
using BlueTusk.ServiceTopology.Domain;
#endif
using Xunit;

namespace BlueTusk.Applications.ArchitectureTests;

public sealed class DependencyDirectionTests
{
    public static TheoryData<Assembly> DomainAssemblies => new()
    {
        typeof(FulfilmentOrder).Assembly,
#if !BLUETUSK_CORE_PACKAGES_ONLY
        typeof(Account).Assembly,
        typeof(ServiceNode).Assembly,
#endif
    };

    public static TheoryData<Assembly> ApplicationAssemblies => new()
    {
        typeof(OrderService).Assembly,
#if !BLUETUSK_CORE_PACKAGES_ONLY
        typeof(FraudService).Assembly,
        typeof(TopologyService).Assembly,
#endif
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

using System.Reflection;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace BlueTusk.EntityFrameworkCore.Infrastructure.Internal;

internal sealed class BlueTuskDesignTimeServicesBridge : IDesignTimeServices
{
    private const string DesignAssemblyName = "BlueTusk.EntityFrameworkCore.Design";
    private const string DesignServicesTypeName =
        "BlueTusk.EntityFrameworkCore.Design.Internal.BlueTuskDesignTimeServices";

    public BlueTuskDesignTimeServicesBridge()
    {
    }

    public void ConfigureDesignTimeServices(IServiceCollection serviceCollection)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        var designAssembly = Assembly.Load(new AssemblyName(DesignAssemblyName));
        var designServicesType = designAssembly.GetType(
            DesignServicesTypeName,
            throwOnError: true,
            ignoreCase: false)!;
        var designServices = (IDesignTimeServices)Activator.CreateInstance(designServicesType)!;

        designServices.ConfigureDesignTimeServices(serviceCollection);
    }
}

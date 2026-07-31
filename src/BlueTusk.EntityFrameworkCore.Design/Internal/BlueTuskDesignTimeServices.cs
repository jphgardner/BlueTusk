using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.Extensions.DependencyInjection;

namespace BlueTusk.EntityFrameworkCore.Design.Internal;

public sealed class BlueTuskDesignTimeServices : IDesignTimeServices
{
    public void ConfigureDesignTimeServices(IServiceCollection serviceCollection)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        new EntityFrameworkRelationalDesignServicesBuilder(serviceCollection)
            .TryAdd<IDatabaseModelFactory, BlueTuskDatabaseModelFactory>()
            .TryAdd<IProviderConfigurationCodeGenerator, BlueTuskProviderCodeGenerator>()
            .TryAdd<IAnnotationCodeGenerator, AnnotationCodeGenerator>()
            .TryAddCoreServices();
    }
}

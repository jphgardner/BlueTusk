using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlueTusk.EntityFrameworkCore.Design.Internal;

public sealed class BlueTuskDesignTimeServices : IDesignTimeServices
{
    public void ConfigureDesignTimeServices(IServiceCollection serviceCollection)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        serviceCollection.AddEntityFrameworkBlueTusk();

        var builder = new EntityFrameworkRelationalDesignServicesBuilder(serviceCollection);
        builder
            .TryAdd<IDatabaseModelFactory, BlueTuskDatabaseModelFactory>()
            .TryAdd<IProviderConfigurationCodeGenerator, BlueTuskProviderCodeGenerator>()
            .TryAdd<IAnnotationCodeGenerator, BlueTuskAnnotationCodeGenerator>()
            .TryAddCoreServices();
        builder.TryAddProviderSpecificServices(
            services => services.ServiceCollection.Replace(
                ServiceDescriptor.Singleton<
                    ICSharpMigrationOperationGenerator,
                    BlueTuskCSharpMigrationOperationGenerator>()));
    }
}

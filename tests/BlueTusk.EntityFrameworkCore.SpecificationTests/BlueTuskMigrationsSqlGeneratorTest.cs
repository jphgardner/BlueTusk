using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore.Migrations;

public sealed partial class BlueTuskMigrationsSqlGeneratorTest()
    : MigrationsSqlGeneratorTestBase(
        BlueTuskTestHelpers.Instance,
        CreateServiceCollection(),
        CreateOptions())
{
    protected override string GetGeometryCollectionStoreType()
        => "geometry(GeometryCollection,4326)";

    private static DbContextOptions CreateOptions()
        => new DbContextOptionsBuilder()
            .UseBlueTusk(
                "Host=localhost;Database=DummyDatabase;Username=postgres;Password=postgres;SSL Mode=Disable;Channel Binding=Disable",
                options => options.UsePostGis())
            .Options;

    private static ServiceCollection CreateServiceCollection()
    {
        var services = new ServiceCollection();
        foreach (var extension in CreateOptions().Extensions.Where(extension => !extension.Info.IsDatabaseProvider))
        {
            extension.ApplyServices(services);
        }

        return services;
    }
}

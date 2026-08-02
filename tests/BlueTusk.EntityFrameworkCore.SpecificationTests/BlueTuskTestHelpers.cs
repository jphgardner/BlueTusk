using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore;

internal sealed class BlueTuskTestHelpers : RelationalTestHelpers
{
    private BlueTuskTestHelpers()
    {
    }

    public static BlueTuskTestHelpers Instance { get; } = new();

    public override IServiceCollection AddProviderServices(IServiceCollection services)
        => services.AddEntityFrameworkBlueTusk();

    public override DbContextOptionsBuilder UseProviderOptions(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseBlueTusk(
            "Host=localhost;Database=DummyDatabase;Username=postgres;Password=postgres;SSL Mode=Disable;Channel Binding=Disable");

    public override LoggingDefinitions LoggingDefinitions { get; } = new BlueTuskTestLoggingDefinitions();

    private sealed class BlueTuskTestLoggingDefinitions : RelationalLoggingDefinitions;
}

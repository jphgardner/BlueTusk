using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BlueTusk.OrderOperations.Infrastructure;

public sealed class OrderDesignTimeDbContextFactory :
    IDesignTimeDbContextFactory<OrderOperationsDbContext>
{
    public OrderOperationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrderOperationsDbContext>()
            .UseBlueTusk(DesignTimeConnectionString.Resolve(args))
            .Options;
        return new OrderOperationsDbContext(options);
    }
}

internal static class DesignTimeConnectionString
{
    public static string Resolve(string[] args) =>
        args.FirstOrDefault(value => value.StartsWith("Host=", StringComparison.OrdinalIgnoreCase))
        ?? "Host=127.0.0.1;Database=bluetusk_design;Username=postgres;Password=design;" +
            "SSL Mode=Disable;Pooling=false";
}

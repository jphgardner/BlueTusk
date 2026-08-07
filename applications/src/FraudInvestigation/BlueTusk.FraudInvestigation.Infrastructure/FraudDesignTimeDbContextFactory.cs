using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BlueTusk.FraudInvestigation.Infrastructure;

public sealed class FraudDesignTimeDbContextFactory :
    IDesignTimeDbContextFactory<FraudDbContext>
{
    public FraudDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.FirstOrDefault(
            value => value.StartsWith("Host=", StringComparison.OrdinalIgnoreCase))
            ?? "Host=127.0.0.1;Database=bluetusk_design;Username=postgres;Password=design;" +
                "SSL Mode=Disable;Pooling=false";
        var options = new DbContextOptionsBuilder<FraudDbContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new FraudDbContext(options);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BlueTusk.ServiceTopology.Infrastructure;

public sealed class TopologyDesignTimeDbContextFactory :
    IDesignTimeDbContextFactory<TopologyDbContext>
{
    public TopologyDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.FirstOrDefault(
            value => value.StartsWith("Host=", StringComparison.OrdinalIgnoreCase))
            ?? "Host=127.0.0.1;Database=bluetusk_design;Username=postgres;Password=design;" +
                "SSL Mode=Disable;Pooling=false";
        var options = new DbContextOptionsBuilder<TopologyDbContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new TopologyDbContext(options);
    }
}

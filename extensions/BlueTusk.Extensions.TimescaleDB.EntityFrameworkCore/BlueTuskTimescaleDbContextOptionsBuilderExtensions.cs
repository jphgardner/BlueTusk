using BlueTusk.EntityFrameworkCore.Infrastructure;
using BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures the separately packaged TimescaleDB EF integration.</summary>
public static class BlueTuskTimescaleDbContextOptionsBuilderExtensions
{
    /// <summary>Adds TimescaleDB time-bucket and hyperfunction translations.</summary>
    public static BlueTuskDbContextOptionsBuilder UseTimescaleDb(
        this BlueTuskDbContextOptionsBuilder optionsBuilder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        var extension = new BlueTuskTimescaleDbOptionsExtension(schema);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder.ContextOptionsBuilder)
            .AddOrUpdateExtension(extension);
        return optionsBuilder;
    }
}

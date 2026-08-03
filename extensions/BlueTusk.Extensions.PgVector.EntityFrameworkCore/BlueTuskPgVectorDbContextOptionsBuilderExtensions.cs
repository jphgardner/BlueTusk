using BlueTusk.EntityFrameworkCore.Infrastructure;
using BlueTusk.Extensions.PgVector.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures the separately packaged pgvector EF integration.</summary>
public static class BlueTuskPgVectorDbContextOptionsBuilderExtensions
{
    /// <summary>Adds pgvector type mappings and distance operators to a BlueTusk EF context.</summary>
    public static BlueTuskDbContextOptionsBuilder UsePgVector(
        this BlueTuskDbContextOptionsBuilder optionsBuilder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        var extension = new BlueTuskPgVectorOptionsExtension(schema);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder.ContextOptionsBuilder)
            .AddOrUpdateExtension(extension);
        return optionsBuilder;
    }
}

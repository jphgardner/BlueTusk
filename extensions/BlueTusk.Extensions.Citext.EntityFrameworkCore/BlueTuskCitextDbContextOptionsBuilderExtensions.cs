using BlueTusk.EntityFrameworkCore.Infrastructure;
using BlueTusk.Extensions.Citext.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures the separately packaged citext EF integration.</summary>
public static class BlueTuskCitextDbContextOptionsBuilderExtensions
{
    /// <summary>Adds citext type mappings to a BlueTusk EF context.</summary>
    public static BlueTuskDbContextOptionsBuilder UseCitext(
        this BlueTuskDbContextOptionsBuilder optionsBuilder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        var extension = new BlueTuskCitextOptionsExtension(schema);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder.ContextOptionsBuilder)
            .AddOrUpdateExtension(extension);
        return optionsBuilder;
    }
}

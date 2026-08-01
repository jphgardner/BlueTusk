using BlueTusk.EntityFrameworkCore.Infrastructure;
using BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures the separately packaged PostGIS EF integration.</summary>
public static class BlueTuskPostGisDbContextOptionsBuilderExtensions
{
    /// <summary>Adds NetTopologySuite mappings and PostGIS spatial translations.</summary>
    public static BlueTuskDbContextOptionsBuilder UsePostGis(
        this BlueTuskDbContextOptionsBuilder optionsBuilder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        var extension = new BlueTuskPostGisOptionsExtension(schema);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder.ContextOptionsBuilder)
            .AddOrUpdateExtension(extension);
        return optionsBuilder;
    }
}

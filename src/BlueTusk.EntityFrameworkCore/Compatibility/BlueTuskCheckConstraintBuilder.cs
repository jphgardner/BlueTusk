#if !BLUETUSK_EF6_OR_GREATER
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlueTusk.EntityFrameworkCore;

/// <summary>Configures a PostgreSQL check constraint on Entity Framework Core 5.</summary>
public sealed class BlueTuskCheckConstraintBuilder
{
    internal BlueTuskCheckConstraintBuilder(IMutableCheckConstraint metadata) => Metadata = metadata;

    /// <summary>The underlying EF Core check constraint.</summary>
    public IMutableCheckConstraint Metadata { get; }
}

/// <summary>Provides fluent check-constraint configuration on Entity Framework Core 5.</summary>
public static class BlueTuskLegacyCheckConstraintExtensions
{
    /// <summary>Adds and configures a named check constraint.</summary>
    public static EntityTypeBuilder HasCheckConstraint(this EntityTypeBuilder builder, string name, string sql,
        Action<BlueTuskCheckConstraintBuilder> configure)
    {
        BlueTuskGuard.NotNull(builder);
        BlueTuskGuard.NotNull(configure);
        var metadata = builder.Metadata.FindCheckConstraint(name) ?? builder.Metadata.AddCheckConstraint(name, sql);
        if (!string.Equals(metadata.Sql, sql, StringComparison.Ordinal))
        {
            builder.Metadata.RemoveCheckConstraint(name);
            metadata = builder.Metadata.AddCheckConstraint(name, sql);
        }

        configure(new BlueTuskCheckConstraintBuilder(metadata));
        return builder;
    }
}
#endif

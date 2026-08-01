using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.Infrastructure;

/// <summary>Configures BlueTusk-specific options for an Entity Framework Core context.</summary>
public sealed class BlueTuskDbContextOptionsBuilder
    : RelationalDbContextOptionsBuilder<BlueTuskDbContextOptionsBuilder, BlueTuskOptionsExtension>
{
    public BlueTuskDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
        : base(optionsBuilder)
    {
    }

    /// <summary>
    /// Gets the underlying options builder so separately packaged provider plug-ins can
    /// register immutable EF options extensions.
    /// </summary>
    public DbContextOptionsBuilder ContextOptionsBuilder => OptionsBuilder;
}

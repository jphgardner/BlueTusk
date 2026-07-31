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
}

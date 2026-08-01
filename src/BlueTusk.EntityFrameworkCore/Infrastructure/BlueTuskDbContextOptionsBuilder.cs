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

    /// <summary>
    /// Configures the existing database used to create or drop the target database.
    /// </summary>
    /// <remarks>
    /// The default is <c>postgres</c>, except when <c>postgres</c> is itself the target,
    /// in which case <c>template1</c> is used.
    /// </remarks>
    public BlueTuskDbContextOptionsBuilder UseAdminDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        var extension = OptionsBuilder.Options.FindExtension<BlueTuskOptionsExtension>()
            ?? throw new InvalidOperationException("BlueTusk provider options have not been configured.");
        extension = extension.WithAdminDatabase(databaseName);
        ((IDbContextOptionsBuilderInfrastructure)OptionsBuilder).AddOrUpdateExtension(extension);
        return this;
    }
}

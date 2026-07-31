using System.Data.Common;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Infrastructure;
using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

/// <summary>BlueTusk-specific extensions for <see cref="DbContextOptionsBuilder" />.</summary>
public static class BlueTuskDbContextOptionsBuilderExtensions
{
    public static DbContextOptionsBuilder UseBlueTusk(
        this DbContextOptionsBuilder optionsBuilder,
        string? connectionString,
        Action<BlueTuskDbContextOptionsBuilder>? blueTuskOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var extension = GetOrCreateExtension(optionsBuilder)
            .WithConnectionString(connectionString);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        blueTuskOptionsAction?.Invoke(new BlueTuskDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }

    public static DbContextOptionsBuilder<TContext> UseBlueTusk<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string? connectionString,
        Action<BlueTuskDbContextOptionsBuilder>? blueTuskOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseBlueTusk(
            (DbContextOptionsBuilder)optionsBuilder,
            connectionString,
            blueTuskOptionsAction);

    public static DbContextOptionsBuilder UseBlueTusk(
        this DbContextOptionsBuilder optionsBuilder,
        BlueTuskConnection connection,
        bool contextOwnsConnection = false,
        Action<BlueTuskDbContextOptionsBuilder>? blueTuskOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connection);

        var extension = GetOrCreateExtension(optionsBuilder)
            .WithConnection(connection, contextOwnsConnection);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        blueTuskOptionsAction?.Invoke(new BlueTuskDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }

    public static DbContextOptionsBuilder<TContext> UseBlueTusk<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        BlueTuskConnection connection,
        bool contextOwnsConnection = false,
        Action<BlueTuskDbContextOptionsBuilder>? blueTuskOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseBlueTusk(
            (DbContextOptionsBuilder)optionsBuilder,
            connection,
            contextOwnsConnection,
            blueTuskOptionsAction);

    private static BlueTuskOptionsExtension GetOrCreateExtension(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.Options.FindExtension<BlueTuskOptionsExtension>()
            ?? new BlueTuskOptionsExtension();
}

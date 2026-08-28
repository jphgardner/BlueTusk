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
            .WithDataSource(null)
            .WithConnectionString(connectionString);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        ConfigureWarnings(optionsBuilder);
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
            .WithDataSource(null)
            .WithConnection(connection, contextOwnsConnection);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        ConfigureWarnings(optionsBuilder);
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

    /// <summary>
    /// Configures BlueTusk with a data source that owns pooling and runtime type mappings.
    /// The application remains responsible for disposing the data source.
    /// </summary>
    public static DbContextOptionsBuilder UseBlueTusk(
        this DbContextOptionsBuilder optionsBuilder,
        BlueTuskDataSource dataSource,
        Action<BlueTuskDbContextOptionsBuilder>? blueTuskOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(dataSource);

        var extension = GetOrCreateExtension(optionsBuilder)
            .WithDataSource(dataSource)
            .WithConnectionString(dataSource.ConnectionString);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        ConfigureWarnings(optionsBuilder);
        blueTuskOptionsAction?.Invoke(new BlueTuskDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }

    public static DbContextOptionsBuilder<TContext> UseBlueTusk<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        BlueTuskDataSource dataSource,
        Action<BlueTuskDbContextOptionsBuilder>? blueTuskOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseBlueTusk(
            (DbContextOptionsBuilder)optionsBuilder,
            dataSource,
            blueTuskOptionsAction);

    private static BlueTuskOptionsExtension GetOrCreateExtension(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.Options.FindExtension<BlueTuskOptionsExtension>()
            ?? new BlueTuskOptionsExtension();

    private static void ConfigureWarnings(DbContextOptionsBuilder optionsBuilder)
    {
        var coreOptions = optionsBuilder.Options.FindExtension<CoreOptionsExtension>()
            ?? new CoreOptionsExtension();
        coreOptions = RelationalOptionsExtension.WithDefaultWarningConfiguration(coreOptions);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(coreOptions);
    }
}

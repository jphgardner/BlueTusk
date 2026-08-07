using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Data.Internal;
using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

#pragma warning disable EF1001 // Tests intentionally exercise provider database-lifecycle infrastructure.

public sealed class DatabaseCreatorTests
{
    [Fact]
    public void Lifecycle_settings_preserve_transport_authentication_and_choose_a_writable_admin_database()
    {
        const string connectionString =
            "Host=primary,standby;Port=5432,5433;Database=Target \"Database;" +
            "Username=postgres;Password=secret;Pooling=true;Target Session Attributes=Standby;" +
            "SSL Mode=Disable;Channel Binding=Disable";

        var lifecycle = ProviderServices.Instance.CreateDatabaseLifecycleSettings(
            connectionString,
            "Admin Database");
        var admin = new BlueTuskConnectionStringBuilder(lifecycle.AdminConnectionString);

        Assert.Equal("Target \"Database", lifecycle.TargetDatabase);
        Assert.Equal("Admin Database", admin.Database);
        Assert.Equal("primary,standby", admin.Host);
        Assert.Equal("5432,5433", admin.Ports);
        Assert.Equal("postgres", admin.Username);
        Assert.Equal("secret", admin.Password);
        Assert.False(admin.Pooling);
        Assert.Equal(BlueTuskTargetSessionAttributes.ReadWrite, admin.TargetSessionAttributes);

        var postgresTarget = ProviderServices.Instance.CreateDatabaseLifecycleSettings(
            "Host=localhost;Database=postgres;Username=postgres;Password=secret");
        Assert.Equal(
            "template1",
            new BlueTuskConnectionStringBuilder(postgresTarget.AdminConnectionString).Database);
        Assert.Throws<InvalidOperationException>(() =>
            ProviderServices.Instance.CreateDatabaseLifecycleSettings(
                connectionString,
                "Target \"Database"));
    }

    [Fact]
    public void Admin_database_option_is_immutable_and_visible_in_debug_metadata()
    {
        var options = new DbContextOptionsBuilder<DatabaseLifecycleContext>()
            .UseBlueTusk(
                "Host=localhost;Database=target;Username=postgres;Password=secret",
                provider => Assert.Same(provider, provider.UseAdminDatabase("maintenance")))
            .Options;
        var extension = options.FindExtension<BlueTuskOptionsExtension>()!;
        var debugInfo = new Dictionary<string, string>();

        extension.Info.PopulateDebugInfo(debugInfo);

        Assert.Equal("maintenance", extension.AdminDatabase);
        Assert.Equal("maintenance", debugInfo["BlueTusk:AdminDatabase"]);
    }

    [Fact]
    public async Task Ensure_created_and_deleted_manage_the_physical_database_and_data_source_catalogue_live()
    {
        var adminConnectionString = GetConnectionString();
        var adminSettings = new BlueTuskConnectionStringBuilder(adminConnectionString);
        var databaseName = $"bluetusk ef lifecycle {Environment.ProcessId} \"db";
        var targetSettings = new BlueTuskConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            MinimumPoolSize = 0,
        };
        var password = targetSettings.Password ?? throw new InvalidOperationException(
            "The database-lifecycle acceptance connection must provide a password.");
        targetSettings.Password = null;
        var synchronousPasswordCalls = 0;
        var asynchronousPasswordCalls = 0;
        await using var dataSource = new BlueTuskDataSourceBuilder(targetSettings.ConnectionString)
            .UsePasswordProvider(_ =>
            {
                synchronousPasswordCalls++;
                return password;
            })
            .UsePasswordProvider((_, _) =>
            {
                asynchronousPasswordCalls++;
                return ValueTask.FromResult(password);
            })
            .Build();
        var options = new DbContextOptionsBuilder<DatabaseLifecycleContext>()
            .UseBlueTusk(
                dataSource,
                provider => provider.UseAdminDatabase(adminSettings.Database))
            .Options;
        await using var context = new DatabaseLifecycleContext(options);

        try
        {
            Assert.False(await context.Database.CanConnectAsync());
            Assert.True(await context.Database.EnsureCreatedAsync());
            Assert.True(await context.Database.CanConnectAsync());
            Assert.False(await context.Database.EnsureCreatedAsync());
            Assert.True(await context.GetService<IRelationalDatabaseCreator>().HasTablesAsync());

            context.Values.Add(new DatabaseLifecycleValue { Id = 1, Name = "created" });
            Assert.Equal(1, await context.SaveChangesAsync());
            Assert.Equal("created", (await context.Values.SingleAsync()).Name);

            await context.Database.ExecuteSqlRawAsync(
                "CREATE TYPE \"lifecycle enum\" AS ENUM ('created')");
            await dataSource.ReloadTypesAsync();
            Assert.Contains(
                dataSource.TypeRegistry.Types,
                type => type.Schema == "public" && type.Name == "lifecycle enum");

            Assert.True(await context.Database.EnsureDeletedAsync());
            Assert.False(await context.Database.CanConnectAsync());
            Assert.False(await context.Database.EnsureDeletedAsync());

            Assert.True(context.Database.EnsureCreated());
            Assert.DoesNotContain(
                dataSource.TypeRegistry.Types,
                type => type.Schema == "public" && type.Name == "lifecycle enum");
            Assert.True(context.GetService<IRelationalDatabaseCreator>().HasTables());
            Assert.True(context.Database.EnsureDeleted());
            Assert.False(context.Database.CanConnect());

            var explicitPasswordSettings = new BlueTuskConnectionStringBuilder(targetSettings.ConnectionString)
            {
                Password = password,
            };
            var stringOptions = new DbContextOptionsBuilder<DatabaseLifecycleContext>()
                .UseBlueTusk(
                    explicitPasswordSettings.ConnectionString,
                    provider => provider.UseAdminDatabase(adminSettings.Database))
                .Options;
            await using (var stringContext = new DatabaseLifecycleContext(stringOptions))
            {
                Assert.True(await stringContext.Database.EnsureCreatedAsync());
                Assert.True(await stringContext.Database.EnsureDeletedAsync());
            }

            await using var connection = new BlueTuskConnection(explicitPasswordSettings.ConnectionString);
            var connectionOptions = new DbContextOptionsBuilder<DatabaseLifecycleContext>()
                .UseBlueTusk(
                    connection,
                    contextOwnsConnection: false,
                    provider => provider.UseAdminDatabase(adminSettings.Database))
                .Options;
            await using (var connectionContext = new DatabaseLifecycleContext(connectionOptions))
            {
                Assert.True(connectionContext.Database.EnsureCreated());
                Assert.True(connectionContext.Database.EnsureDeleted());
            }

            connection.ConnectionString = adminConnectionString;
            await connection.OpenAsync();
            Assert.Equal(adminSettings.Database, connection.Database);
            await connection.CloseAsync();
            Assert.True(synchronousPasswordCalls > 0);
            Assert.True(asynchronousPasswordCalls > 0);
        }
        finally
        {
            await DropDatabaseIfPresentAsync(
                adminConnectionString,
                databaseName,
                CancellationToken.None);
        }
    }

    private static async Task DropDatabaseIfPresentAsync(
        string adminConnectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var dataSource = BlueTuskDataSource.Create(adminConnectionString);
        await using var command = dataSource.CreateCommand(
            $"DROP DATABASE IF EXISTS {DelimitIdentifier(databaseName)} WITH (FORCE)");
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string DelimitIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        }.ConnectionString;
    }

    private sealed class DatabaseLifecycleContext(DbContextOptions<DatabaseLifecycleContext> options)
        : DbContext(options)
    {
        public DbSet<DatabaseLifecycleValue> Values => Set<DatabaseLifecycleValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DatabaseLifecycleValue>(entity =>
            {
                entity.ToTable("database_lifecycle_values");
                entity.HasKey(value => value.Id);
                entity.Property(value => value.Id).ValueGeneratedNever();
                entity.Property(value => value.Name).HasMaxLength(64);
            });
        }
    }

    private sealed class DatabaseLifecycleValue
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}

#pragma warning restore EF1001

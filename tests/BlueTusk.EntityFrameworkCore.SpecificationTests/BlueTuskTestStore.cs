using System.Data;
using System.Data.Common;
using BlueTusk.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace Microsoft.EntityFrameworkCore.TestUtilities;

internal sealed class BlueTuskTestStore(string name, bool shared)
    : RelationalTestStore(name, shared, new BlueTuskConnection(CreateConnectionString(name)))
{
    public const string ConnectionStringEnvironmentVariable = "BLUETUSK_TEST_CONNECTION_STRING";

    public static bool IsConfigured
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable));

    public override DbContextOptionsBuilder AddProviderOptions(DbContextOptionsBuilder builder)
        => UseConnectionString
            ? builder.UseBlueTusk(ConnectionString)
            : builder.UseBlueTusk((BlueTuskConnection)Connection);

    protected override async Task InitializeAsync(
        Func<DbContext> createContext,
        Func<DbContext, Task>? seed,
        Func<DbContext, Task>? clean)
    {
        await RecreateDatabaseAsync().ConfigureAwait(false);

        await using var context = createContext();
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        if (seed is not null)
        {
            await seed(context).ConfigureAwait(false);
        }
    }

    public override void OpenConnection()
        => Connection.Open();

    public override Task OpenConnectionAsync()
        => Connection.OpenAsync();

    private async Task RecreateDatabaseAsync()
    {
        if (Connection.State != ConnectionState.Closed)
        {
            await Connection.CloseAsync().ConfigureAwait(false);
        }

        await using var administration = new BlueTuskConnection(CreateConnectionString("postgres"));
        await administration.OpenAsync().ConfigureAwait(false);

        var identifier = DelimitIdentifier(Name);
        await ExecuteNonQueryAsync(administration, $"DROP DATABASE IF EXISTS {identifier} WITH (FORCE)")
            .ConfigureAwait(false);
        await ExecuteNonQueryAsync(administration, $"CREATE DATABASE {identifier}")
            .ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string CreateConnectionString(string database)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip($"{ConnectionStringEnvironmentVariable} is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(connectionString)
        {
            Database = database,
        }.ConnectionString;
    }

    private static string DelimitIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

internal sealed class BlueTuskTestStoreFactory : RelationalTestStoreFactory
{
    private BlueTuskTestStoreFactory()
    {
    }

    public static BlueTuskTestStoreFactory Instance { get; } = new();

    public override TestStore Create(string storeName)
        => new BlueTuskTestStore(storeName, shared: false);

    public override TestStore GetOrCreate(string storeName)
        => new BlueTuskTestStore(storeName, shared: true);

    public override IServiceCollection AddProviderServices(IServiceCollection serviceCollection)
        => serviceCollection.AddEntityFrameworkBlueTusk();
}

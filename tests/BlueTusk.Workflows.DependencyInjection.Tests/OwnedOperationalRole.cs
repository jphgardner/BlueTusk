using System.Security.Cryptography;
using BlueTusk.Data;
using BlueTusk.Workflows.Tests;

namespace BlueTusk.Workflows.DependencyInjection.Tests;

internal sealed class OwnedOperationalRole : IAsyncDisposable
{
    private readonly WorkflowDatabase _database;
    private string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private BlueTuskDataSource? _source;
    private int _providerCalls;
    private bool _created;

    private OwnedOperationalRole(WorkflowDatabase database) { _database = database; }
    internal string Name { get; } = "bt_job_ops_" + Guid.NewGuid().ToString("N");
    internal BlueTuskDataSource Source => _source!;
    internal int ProviderCalls => Volatile.Read(ref _providerCalls);
    internal BlueTuskDataSource CreateFixedCredentialSource()
    {
        var settings = new BlueTuskConnectionStringBuilder(Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING")!)
        {
            Username = Name,
            Password = Volatile.Read(ref _password),
            MaximumPoolSize = 1,
            MinimumPoolSize = 0,
            ApplicationName = "BlueTuskOwnedStaleCredential",
        };
        return BlueTuskDataSource.Create(settings.ConnectionString);
    }

    internal static async Task<OwnedOperationalRole> CreateAsync(WorkflowDatabase database)
    {
        var role = new OwnedOperationalRole(database);
        try
        {
            await database.ExecuteAsync($"CREATE ROLE \"{role.Name}\" LOGIN");
            role._created = true;
            await role.SetPasswordAsync(role._password);
            var settings = new BlueTuskConnectionStringBuilder(Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING")!)
            {
                Username = role.Name,
                Password = string.Empty,
                MaximumPoolSize = 4,
                MinimumPoolSize = 0,
                ApplicationName = "BlueTuskOwnedJobsWorkflowRole",
            };
            role._source = new BlueTuskDataSourceBuilder(settings.ConnectionString).UsePasswordProvider((_, token) =>
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref role._providerCalls);
                return ValueTask.FromResult(Volatile.Read(ref role._password));
            }).Build();
            return role;
        }
        catch { await role.DisposeAsync(); throw; }
    }

    internal async Task RotateAsync()
    {
        string replacement = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await SetPasswordAsync(replacement);
        Volatile.Write(ref _password, replacement);
        await Source.ClearPoolAsync();
    }

    internal async Task TerminateOwnedSessionsAsync()
    {
        await using var connection = await _database.Source.OpenConnectionAsync();
        await using var command = new BlueTuskCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename = @role AND pid <> pg_backend_pid()", connection);
        command.Parameters.Add(new BlueTuskParameter<string>(Name) { ParameterName = "role" });
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    internal async Task GrantHealthAsync() => await _database.ExecuteAsync($$"""
        GRANT USAGE ON SCHEMA {jobs}, {schema} TO "{{Name}}";
        GRANT SELECT ON {jobs}.jobs, {schema}.instances TO "{{Name}}";
        """);

    internal async Task GrantRuntimeAsync() => await _database.ExecuteAsync($$"""
        GRANT USAGE ON SCHEMA {jobs}, {schema} TO "{{Name}}";
        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {jobs}, {schema} TO "{{Name}}";
        """);

    private async Task SetPasswordAsync(string password)
    {
        await using var connection = await _database.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var configure = new BlueTuskCommand("SELECT set_config('bluetusk_owned.role', @role, true), set_config('bluetusk_owned.password', @password, true)", connection) { Transaction = transaction };
        configure.Parameters.Add(new BlueTuskParameter<string>(Name) { ParameterName = "role" });
        configure.Parameters.Add(new BlueTuskParameter<string>(password) { ParameterName = "password" });
        _ = await configure.ExecuteNonQueryAsync(CancellationToken.None);
        await using var alter = new BlueTuskCommand("""
            DO $owned$ BEGIN
                EXECUTE format('ALTER ROLE %I PASSWORD %L', current_setting('bluetusk_owned.role'), current_setting('bluetusk_owned.password'));
            END $owned$
            """, connection) { Transaction = transaction };
        _ = await alter.ExecuteNonQueryAsync(CancellationToken.None);
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_source is not null) { await _source.DisposeAsync(); }
        if (_created)
        {
            await _database.ExecuteAsync($"DROP OWNED BY \"{Name}\"; DROP ROLE \"{Name}\"");
        }
    }
}

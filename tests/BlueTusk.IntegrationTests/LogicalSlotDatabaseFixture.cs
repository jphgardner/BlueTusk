using System.Globalization;
using BlueTusk.Client;
using BlueTusk.Data;

namespace BlueTusk.IntegrationTests;

/// <summary>
/// A database owned by the logical-slot tests of one test class. A logical slot decodes every
/// transaction in its database, and PostgreSQL 15-19 can create a slot that never decodes a
/// table another session created and wrote while the slot was being built. The other test
/// classes in this assembly run in parallel and do exactly that in the shared test database, so
/// the slot tests create their slots here instead. Tests in one class run one at a time, so only
/// the owning class writes to this database. It is created on first use and dropped once,
/// because DROP DATABASE forces a checkpoint.
/// </summary>
public sealed class LogicalSlotDatabaseFixture : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _administrationConnectionString;
    private string? _connectionString;
    private string? _name;

    /// <summary>
    /// Returns <paramref name="sharedConnectionString"/> pointed at this class's database,
    /// creating the database through the shared one on first use.
    /// </summary>
    internal async Task<string> GetConnectionStringAsync(string sharedConnectionString)
    {
        await _gate.WaitAsync();
        try
        {
            if (_connectionString is not null)
            {
                return _connectionString;
            }

            var name = "bluetusk_slots_" + Guid.NewGuid().ToString("N");
            await ExecuteAsync(sharedConnectionString, $"CREATE DATABASE {BlueTuskSql.QuoteIdentifier(name)}");
            _administrationConnectionString = sharedConnectionString;
            _name = name;
            _connectionString = new BlueTuskConnectionStringBuilder(sharedConnectionString) { Database = name }.ConnectionString;
            return _connectionString;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_administrationConnectionString is null || _name is null)
            {
                return;
            }

            // DROP DATABASE rejects a database that still has a replication slot. A temporary slot
            // is dropped when its walsender exits, which can trail the client's disconnect, and a
            // failed test can leave a persistent slot behind.
            var database = BlueTuskSql.QuoteLiteral(_name);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (await ScalarAsync(
                _administrationConnectionString,
                $"SELECT count(*) FROM pg_catalog.pg_replication_slots WHERE database = {database}",
                deadline.Token) != 0)
            {
                await ExecuteAsync(
                    _administrationConnectionString,
                    "SELECT pg_catalog.pg_terminate_backend(active_pid) FROM pg_catalog.pg_replication_slots " +
                    $"WHERE database = {database} AND active_pid IS NOT NULL; " +
                    "SELECT pg_catalog.pg_drop_replication_slot(slot_name) FROM pg_catalog.pg_replication_slots " +
                    $"WHERE database = {database} AND NOT active AND NOT temporary",
                    deadline.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }

            await ExecuteAsync(
                _administrationConnectionString,
                $"DROP DATABASE IF EXISTS {BlueTuskSql.QuoteIdentifier(_name)} WITH (FORCE)",
                CancellationToken.None);
        }
        finally
        {
            _gate.Dispose();
        }
    }

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new BlueTuskCommand(sql, connection) { CommandTimeout = 300 };
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new BlueTuskCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }
}

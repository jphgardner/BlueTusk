using System.Globalization;
using BlueTusk.Data;

namespace BlueTusk.Documents.Tests;

/// <summary>
/// A database owned by the WAL tests of one test class. A logical slot decodes every
/// transaction in its database, and PostgreSQL 15-19 can create a slot that never decodes
/// a table another session created and wrote while the slot was being built. Other test
/// classes do exactly that in the shared fixture database, so the WAL tests create their
/// slots here instead. The database is created on first use, so tests that need no
/// database still run without a connection string, and it is dropped once because
/// DROP DATABASE forces a checkpoint.
/// </summary>
public sealed class DocumentWalDatabaseFixture : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BlueTuskDataSource? _admin;
    private string? _name;
    private BlueTuskDataSource? _source;

    internal static string RequiredConnectionString() =>
        Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Set BLUETUSK_TEST_CONNECTION_STRING to a disposable logical-replication PostgreSQL database.");

    internal async Task<BlueTuskDataSource> GetSourceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_source is not null)
            {
                return _source;
            }

            var connectionString = RequiredConnectionString();
            var name = "documents_wal_" + Guid.NewGuid().ToString("N");
            var admin = BlueTuskDataSource.Create(connectionString);
            try
            {
                await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\"", cancellationToken);
            }
            catch
            {
                await admin.DisposeAsync();
                throw;
            }

            _admin = admin;
            _name = name;
            _source = BlueTuskDataSource.Create(new BlueTuskConnectionStringBuilder(connectionString) { Database = name }.ConnectionString);
            return _source;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static async Task ExecuteAsync(BlueTuskDataSource source, string sql, CancellationToken cancellationToken, int commandTimeoutSeconds = 30)
    {
        await using var command = source.CreateCommand(sql);
        command.CommandTimeout = commandTimeoutSeconds;
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync();
        }

        if (_admin is null)
        {
            _gate.Dispose();
            return;
        }

        try
        {
            // A temporary slot is dropped when its walsender exits, which can trail the
            // client's disconnect, and DROP DATABASE rejects a database with an active slot.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                await using var active = _admin.CreateCommand(
                    $"SELECT count(pg_terminate_backend(active_pid)) FROM pg_replication_slots WHERE database = '{_name}' AND active_pid IS NOT NULL");
                if (Convert.ToInt64(await active.ExecuteScalarAsync(deadline.Token), CultureInfo.InvariantCulture) == 0)
                {
                    break;
                }

                await Task.Delay(20, deadline.Token);
            }

            await ExecuteAsync(_admin, $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", CancellationToken.None, commandTimeoutSeconds: 300);
        }
        finally
        {
            await _admin.DisposeAsync();
            _gate.Dispose();
        }
    }
}

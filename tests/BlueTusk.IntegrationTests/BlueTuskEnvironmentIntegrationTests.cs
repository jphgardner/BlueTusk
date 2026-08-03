using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskEnvironmentIntegrationTests
{
    [Fact]
    public async Task PgBouncer_session_mode_supports_session_affine_provider_features()
    {
        var connectionString = RequireEnvironmentVariable(
            "BLUETUSK_PGBOUNCER_SESSION_CONNECTION_STRING");
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteNonQueryAsync(
            connection,
            "CREATE TEMP TABLE bluetusk_pgbouncer_session (value int4 NOT NULL)");

        await using (var insert = new BlueTuskCommand(
            "INSERT INTO bluetusk_pgbouncer_session (value) VALUES ($1)",
            connection))
        {
            var value = new BlueTuskParameter<int>(20);
            insert.Parameters.Add(value);
            await insert.PrepareAsync(CancellationToken.None);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(CancellationToken.None));
            value.TypedValue = 22;
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(CancellationToken.None));
        }

        await using var sum = new BlueTuskCommand(
            "SELECT sum(value)::int8 FROM bluetusk_pgbouncer_session",
            connection);
        Assert.Equal(42L, await sum.ExecuteScalarAsync<long>(CancellationToken.None));
    }

    [Fact]
    public async Task PgBouncer_transaction_mode_supports_transactions_and_prepared_commands()
    {
        var connectionString = RequireEnvironmentVariable(
            "BLUETUSK_PGBOUNCER_TRANSACTION_CONNECTION_STRING");
        var table = $"bluetusk_pgbouncer_{Guid.NewGuid():N}";
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteNonQueryAsync(connection, $"CREATE TABLE {table} (value int4 NOT NULL)");

        try
        {
            await using (var transaction = await connection.BeginTransactionAsync(
                CancellationToken.None))
            await using (var insert = new BlueTuskCommand(
                $"INSERT INTO {table} (value) VALUES ($1)",
                connection)
            {
                Transaction = transaction,
            })
            {
                insert.Parameters.Add(new BlueTuskParameter<int>(42));
                await insert.PrepareAsync(CancellationToken.None);
                Assert.Equal(1, await insert.ExecuteNonQueryAsync(CancellationToken.None));
                await transaction.CommitAsync(CancellationToken.None);
            }

            await using var count = new BlueTuskCommand(
                $"SELECT count(*) FROM {table} WHERE value = $1",
                connection);
            count.Parameters.Add(new BlueTuskParameter<int>(42));
            Assert.Equal(1L, await count.ExecuteScalarAsync<long>(CancellationToken.None));
        }
        finally
        {
            await ExecuteNonQueryAsync(connection, $"DROP TABLE IF EXISTS {table}");
        }
    }

    [Fact]
    public async Task Locale_and_time_zone_image_preserves_text_and_temporal_values()
    {
        var connectionString = RequireEnvironmentVariable(
            "BLUETUSK_LOCALE_TEST_CONNECTION_STRING");
        var expectedLocale = RequireEnvironmentVariable("BLUETUSK_EXPECTED_LOCALE");
        var expectedTimeZone = RequireEnvironmentVariable("BLUETUSK_EXPECTED_TIME_ZONE");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);

        var collation = await ReadStringAsync(
            connection,
            "SELECT datcollate FROM pg_database WHERE datname = current_database()");
        var monetary = await ReadStringAsync(connection, "SHOW lc_monetary");
        var timeZone = await ReadStringAsync(connection, "SHOW TimeZone");
        var localeStem = expectedLocale.Split('.')[0];
        Assert.Contains(localeStem, collation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(localeStem, monetary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedTimeZone, timeZone);

        await using (var money = new BlueTuskCommand("SELECT 1234.56::money", connection))
        {
            Assert.Equal(
                new BlueTuskMoney(123_456, 2),
                await money.ExecuteScalarAsync<BlueTuskMoney>(CancellationToken.None));
        }

        await using var timestamp = new BlueTuskCommand(
            "SELECT '2026-01-15 12:34:56+00'::timestamptz",
            connection);
        var actual = await timestamp.ExecuteScalarAsync<DateTimeOffset>(CancellationToken.None);
        Assert.Equal(
            new DateTimeOffset(2026, 1, 15, 12, 34, 56, TimeSpan.Zero),
            actual.ToUniversalTime());
    }

    private static async Task ExecuteNonQueryAsync(BlueTuskConnection connection, string sql)
    {
        await using var command = new BlueTuskCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<string> ReadStringAsync(
        BlueTuskConnection connection,
        string sql)
    {
        await using var command = new BlueTuskCommand(sql, connection);
        return await command.ExecuteScalarAsync<string>(CancellationToken.None) ??
            throw new InvalidOperationException($"Query returned no value: {sql}");
    }

    private static string RequireEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw SkipException.ForSkip($"{name} is not configured.")
            : value;
    }
}

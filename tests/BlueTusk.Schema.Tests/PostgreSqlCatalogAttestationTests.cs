using System.Data;
using System.Data.Common;
using System.Diagnostics;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Schema.Tests;

[CollectionDefinition("Schema catalogue mutation", DisableParallelization = true)]
public sealed class SchemaCatalogMutationTestGroup;

[Collection("Schema catalogue mutation")]
public sealed class PostgreSqlCatalogAttestationTests
{
    [Fact]
    public async Task Unchanged_catalogue_attests_and_preserves_borrowed_transaction_settings_and_ownership()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var connection = await fixture.Source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await ReadOnlyAsync(connection);
        using var attestation = await PostgreSqlCatalogConsistencyAttestation.BeginAsync(fixture.Source, connection, transaction,
            Options(), TestContext.Current.CancellationToken);
        await attestation.VerifyAsync();
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT current_setting('transaction_read_only') = 'on' AND current_setting('search_path') = 'pg_catalog'";
        Assert.Equal(true, await check.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => attestation.VerifyAsync().AsTask());
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Current_function_cache_and_publication_expansion_cannot_be_claimed_as_held_snapshot_metadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        await ExecuteAsync(fixture.Source, """
            CREATE FUNCTION app.calculate() RETURNS integer LANGUAGE SQL AS 'SELECT 1';
            CREATE TABLE app.items(id bigint PRIMARY KEY, tenant text);
            CREATE PUBLICATION attest_changes FOR TABLE app.items WHERE (tenant = 'a');
            """);
        await using var connection = await fixture.Source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await ReadOnlyAsync(connection);
        using var attestation = await PostgreSqlCatalogConsistencyAttestation.BeginAsync(fixture.Source, connection, transaction,
            Options(), TestContext.Current.CancellationToken);
        await ExecuteAsync(fixture.Source, """
            CREATE OR REPLACE FUNCTION app.calculate() RETURNS integer LANGUAGE SQL AS 'SELECT 2';
            ALTER PUBLICATION attest_changes SET TABLE app.items WHERE (tenant = 'b');
            """);
        await using var cache = connection.CreateCommand();
        cache.Transaction = transaction;
        cache.CommandText = "SELECT pg_get_functiondef(oid) FROM pg_proc WHERE proname = 'calculate' AND pronamespace = 'app'::regnamespace";
        Assert.Contains("SELECT 2", Assert.IsType<string>(await cache.ExecuteScalarAsync(TestContext.Current.CancellationToken)), StringComparison.Ordinal);
        await Assert.ThrowsAsync<SchemaCatalogAttestationMismatchException>(() => attestation.VerifyAsync().AsTask());
    }

    [Fact]
    public async Task Function_change_then_revert_still_changes_tuple_version()
    {
        await using var fixture = await Fixture.CreateAsync();
        await ExecuteAsync(fixture.Source, "CREATE FUNCTION app.calculate() RETURNS integer LANGUAGE SQL AS 'SELECT 1'");
        await using var connection = await fixture.Source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await ReadOnlyAsync(connection);
        using var attestation = await PostgreSqlCatalogConsistencyAttestation.BeginAsync(fixture.Source, connection, transaction,
            Options(), TestContext.Current.CancellationToken);
        await ExecuteAsync(fixture.Source, "CREATE OR REPLACE FUNCTION app.calculate() RETURNS integer LANGUAGE SQL AS 'SELECT 2'");
        await ExecuteAsync(fixture.Source, "CREATE OR REPLACE FUNCTION app.calculate() RETURNS integer LANGUAGE SQL AS 'SELECT 1'");
        await Assert.ThrowsAsync<SchemaCatalogAttestationMismatchException>(() => attestation.VerifyAsync().AsTask());
    }

    [Fact]
    public async Task Shared_role_rename_then_revert_is_attested_by_xmin_instead_of_matching_names()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.CreateRoleAsync();
        await using var connection = await fixture.Source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await ReadOnlyAsync(connection);
        using var attestation = await PostgreSqlCatalogConsistencyAttestation.BeginAsync(fixture.Source, connection, transaction,
            Options(), TestContext.Current.CancellationToken);
        await ExecuteAsync(fixture.Admin, $"ALTER ROLE \"{role}\" RENAME TO \"{role}_new\"");
        try
        {
            await using var cache = connection.CreateCommand();
            cache.Transaction = transaction;
            cache.CommandText = $"SELECT pg_get_userbyid(oid)::text FROM pg_roles WHERE rolname = '{role}'";
            Assert.Contains(role + "_new", Assert.IsType<string>(await cache.ExecuteScalarAsync(TestContext.Current.CancellationToken)), StringComparison.Ordinal);
        }
        finally { await ExecuteAsync(fixture.Admin, $"ALTER ROLE \"{role}_new\" RENAME TO \"{role}\""); }
        await Assert.ThrowsAsync<SchemaCatalogAttestationMismatchException>(() => attestation.VerifyAsync().AsTask());
    }

    [Fact]
    public async Task Ordinary_login_requires_only_oid_xmin_grant_and_cannot_read_credential_columns()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.CreateRoleAsync();
        var settings = new BlueTuskConnectionStringBuilder(fixture.ConnectionString) { Username = role, Password = "attestation-fixture-only" };
        await using var restricted = BlueTuskDataSource.Create(settings.ConnectionString);
        await using (var connection = await restricted.OpenConnectionAsync(TestContext.Current.CancellationToken))
        await using (var transaction = await ReadOnlyAsync(connection))
        {
            await Assert.ThrowsAnyAsync<DbException>(() => PostgreSqlCatalogConsistencyAttestation.BeginAsync(restricted, connection,
                transaction, Options(), TestContext.Current.CancellationToken).AsTask());
        }
        await ExecuteAsync(fixture.Source, $"GRANT SELECT (oid, xmin) ON pg_catalog.pg_authid TO \"{role}\"");
        await using (var connection = await restricted.OpenConnectionAsync(TestContext.Current.CancellationToken))
        await using (var transaction = await ReadOnlyAsync(connection))
        {
            using var attestation = await PostgreSqlCatalogConsistencyAttestation.BeginAsync(restricted, connection, transaction,
                Options(), TestContext.Current.CancellationToken);
            await attestation.VerifyAsync();
            await using var privileges = connection.CreateCommand();
            privileges.Transaction = transaction;
            privileges.CommandText = """
                SELECT has_column_privilege(current_user,'pg_catalog.pg_authid','oid','SELECT'),
                    has_column_privilege(current_user,'pg_catalog.pg_authid','xmin','SELECT'),
                    has_column_privilege(current_user,'pg_catalog.pg_authid','rolpassword','SELECT'),
                    has_table_privilege(current_user,'pg_catalog.pg_authid','SELECT')
                """;
            await using var reader = await privileges.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.True(reader.GetBoolean(0)); Assert.True(reader.GetBoolean(1));
            Assert.False(reader.GetBoolean(2)); Assert.False(reader.GetBoolean(3));
        }
    }

    [Fact]
    public async Task Metadata_and_pool_wait_are_bounded_and_cancellation_does_not_own_the_caller()
    {
        await using var fixture = await Fixture.CreateAsync();
        var settings = new BlueTuskConnectionStringBuilder(fixture.ConnectionString) { MaximumPoolSize = 1 };
        await using var source = BlueTuskDataSource.Create(settings.ConnectionString);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await ReadOnlyAsync(connection);
        var small = Options() with { Limits = new() { MaximumMetadataBytes = 2048 } };
        await Assert.ThrowsAsync<SchemaCaptureLimitException>(() => PostgreSqlCatalogConsistencyAttestation.BeginAsync(source,
            connection, transaction, small, TestContext.Current.CancellationToken).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PostgreSqlCatalogConsistencyAttestation.BeginAsync(source,
            connection, transaction, Options(), cancelled.Token).AsTask());
        using var attestation = await PostgreSqlCatalogConsistencyAttestation.BeginAsync(source, connection, transaction,
            Options() with { Relations = new() { Schemas = ["app"], CommandTimeoutSeconds = 1 } }, TestContext.Current.CancellationToken);
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attestation.VerifyAsync().AsTask());
        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 5);
        Assert.Same(connection, transaction.Connection);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task Verification_routed_to_another_database_fails_closed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var other = await Fixture.CreateAsync();
        await using var connection = await fixture.Source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await ReadOnlyAsync(connection);
        using var attestation = await PostgreSqlCatalogConsistencyAttestation.BeginAsync(other.Source, connection, transaction,
            Options(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SchemaCatalogAttestationMismatchException>(() => attestation.VerifyAsync().AsTask());
    }

    private static SchemaCatalogCaptureOptions Options() => new() { Relations = new() { Schemas = ["app"] } };

    private static async ValueTask<DbTransaction> ReadOnlyAsync(DbConnection connection)
    {
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, TestContext.Current.CancellationToken);
        await using var setup = connection.CreateCommand();
        setup.Transaction = transaction;
        setup.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL search_path = pg_catalog";
        _ = await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return transaction;
    }

    private static async ValueTask ExecuteAsync(BlueTuskDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _database;
        private readonly List<string> _roles = [];
        private Fixture(BlueTuskDataSource admin, string database, string connectionString)
        { Admin = admin; _database = database; ConnectionString = connectionString; Source = BlueTuskDataSource.Create(connectionString); }
        internal BlueTuskDataSource Admin { get; }
        internal BlueTuskDataSource Source { get; }
        internal string ConnectionString { get; }

        internal static async ValueTask<Fixture> CreateAsync()
        {
            var configured = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } value
                ? value : throw SkipException.ForSkip("A disposable PostgreSQL fixture is required.");
            var admin = BlueTuskDataSource.Create(configured);
            var database = "schema_attest_" + Guid.NewGuid().ToString("N");
            await ExecuteAsync(admin, $"CREATE DATABASE \"{database}\"");
            var settings = new BlueTuskConnectionStringBuilder(configured) { Database = database };
            var fixture = new Fixture(admin, database, settings.ConnectionString);
            try { await ExecuteAsync(fixture.Source, "CREATE SCHEMA app"); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal async ValueTask<string> CreateRoleAsync()
        {
            var role = "schema_attest_role_" + Guid.NewGuid().ToString("N");
            await ExecuteAsync(Admin, $"CREATE ROLE \"{role}\" LOGIN PASSWORD 'attestation-fixture-only'");
            _roles.Add(role); return role;
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await ExecuteAsync(Admin, $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
            foreach (var role in _roles)
            {
                await ExecuteAsync(Admin, $"DROP ROLE IF EXISTS \"{role}\"");
            }
            await Admin.DisposeAsync();
        }
    }
}

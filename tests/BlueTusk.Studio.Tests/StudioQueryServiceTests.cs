using System.Data.Common;
using System.Text.Json;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Studio.Tests;

public sealed class StudioQueryServiceTests
{
    [Fact]
    public async Task Resolved_database_role_enforces_RLS_and_privileges_beyond_schema_visibility()
    {
        await using var admin = BlueTuskDataSource.Create(ConnectionString());
        var identity = Guid.NewGuid().ToString("N");
        var schema = "studio_rls_" + identity;
        var role = "studio_role_" + identity;
        var password = Guid.NewGuid().ToString("N");
        try
        {
            await using (var setup = admin.CreateCommand($"""
                CREATE ROLE "{role}" LOGIN PASSWORD '{password}';
                CREATE SCHEMA "{schema}";
                CREATE TABLE "{schema}".orders(id integer PRIMARY KEY, tenant text NOT NULL, value text NOT NULL);
                CREATE TABLE "{schema}".private_values(value text);
                INSERT INTO "{schema}".orders VALUES(1,'a','permitted'),(2,'b','other-tenant-secret');
                INSERT INTO "{schema}".private_values VALUES('private-secret');
                ALTER TABLE "{schema}".orders ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_a ON "{schema}".orders FOR SELECT TO "{role}" USING(tenant='a');
                GRANT USAGE ON SCHEMA "{schema}" TO "{role}";
                GRANT SELECT ON "{schema}".orders TO "{role}"
                """))
            {
                _ = await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var connection = new BlueTuskConnectionStringBuilder(ConnectionString()) { Username = role, Password = password, Pooling = false };
            await using var principal = BlueTuskDataSource.Create(connection.ConnectionString);
            using var service = new StudioQueryService(Options());
            var scope = new StudioDatabaseScope(principal, [schema]);
            var result = await service.ExecuteAsync(scope, new($"SELECT id,value FROM \"{schema}\".orders ORDER BY id"), TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(result.Json);
            Assert.Equal(1, result.Rows);
            Assert.Equal("permitted", json.RootElement.GetProperty("rows")[0][1].GetString());
            await Assert.ThrowsAnyAsync<DbException>(() => service.ExecuteAsync(scope, new($"SELECT * FROM \"{schema}\".private_values"), TestContext.Current.CancellationToken).AsTask());
            await Assert.ThrowsAnyAsync<DbException>(() => service.ExecuteAsync(scope, new("SELECT pg_catalog.pg_read_file('pg_hba.conf')"), TestContext.Current.CancellationToken).AsTask());
            await Assert.ThrowsAnyAsync<DbException>(() => service.ExecuteAsync(scope, new("SELECT pg_catalog.set_config('role','postgres',true)"), TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(1, (await service.ExecuteAsync(scope, new($"SELECT id FROM \"{schema}\".orders"), TestContext.Current.CancellationToken)).Rows);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE; DROP ROLE IF EXISTS \"{role}\"");
            _ = await drop.ExecuteNonQueryAsync();
        }
    }
    [Fact]
    public async Task Queries_are_bounded_and_preserve_exact_numbers_and_nulls()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        using var service = new StudioQueryService(Options());
        var result = await service.ExecuteAsync(new(dataSource, ["public"]), new(
            "SELECT n, 9223372036854775807::bigint AS exact, NULL::text AS empty FROM generate_series(1,1000) n", MaximumRows: 5),
            TestContext.Current.CancellationToken);
        Assert.Equal(5, result.Rows);
        using var json = JsonDocument.Parse(result.Json);
        var rows = json.RootElement.GetProperty("rows");
        Assert.Equal("9223372036854775807", rows[0][1].GetString());
        Assert.Equal(JsonValueKind.Null, rows[0][2].ValueKind);
    }

    [Fact]
    public async Task Rejects_transaction_escape_and_database_write_CTEs()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        using var service = new StudioQueryService(Options());
        var scope = new StudioDatabaseScope(dataSource, ["public"]);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync(scope, new("SELECT 1; COMMIT; CREATE TABLE escaped(id int)"),
            TestContext.Current.CancellationToken).AsTask());
        var schema = "studio_write_" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var setup = dataSource.CreateCommand($"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".values(id int)"))
            {
                _ = await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await Assert.ThrowsAnyAsync<DbException>(() => service.ExecuteAsync(scope,
                new($"WITH changed AS (INSERT INTO \"{schema}\".values VALUES (1) RETURNING id) SELECT id FROM changed"), TestContext.Current.CancellationToken).AsTask());
            await using var verify = dataSource.CreateCommand($"SELECT count(*) FROM \"{schema}\".values");
            Assert.Equal(0L, await verify.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            _ = await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Explain_returns_plan_without_executing_sleep()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        using var service = new StudioQueryService(Options() with { QueryTimeoutSeconds = 1 });
        var result = await service.ExecuteAsync(new(dataSource, ["public"]), new("SELECT pg_sleep(10)", Explain: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Rows);
        using var json = JsonDocument.Parse(result.Json);
        Assert.Contains("Plan", json.RootElement.GetProperty("rows")[0][0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overload_is_rejected_without_queuing_and_capacity_recovers()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        using var service = new StudioQueryService(Options() with { MaximumConcurrentQueries = 1 });
        var scope = new StudioDatabaseScope(dataSource, ["public"]);
        await WhileQueryIsBlockedAsync(dataSource, service, scope,
            () => Assert.ThrowsAsync<StudioCapacityException>(() => service.ExecuteAsync(scope, new("SELECT 2"), TestContext.Current.CancellationToken).AsTask()));
        Assert.Equal(1, (await service.ExecuteAsync(scope, new("SELECT 3"), TestContext.Current.CancellationToken)).Rows);
    }

    [Fact]
    public async Task Oversized_replies_fail_and_do_not_poison_connection_pool()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        using var service = new StudioQueryService(Options() with { MaximumReplyBytes = 1024 });
        var scope = new StudioDatabaseScope(dataSource, ["public"]);
        await Assert.ThrowsAsync<StudioReplyLimitException>(() => service.ExecuteAsync(scope, new("SELECT repeat('x',2048)"), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, (await service.ExecuteAsync(scope, new("SELECT 1"), TestContext.Current.CancellationToken)).Rows);
    }

    [Fact]
    public async Task Encoded_bytea_and_excessive_column_metadata_are_rejected()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        using var service = new StudioQueryService(Options() with { MaximumReplyBytes = 1024 });
        var scope = new StudioDatabaseScope(dataSource, ["public"]);
        await Assert.ThrowsAsync<StudioReplyLimitException>(() => service.ExecuteAsync(scope,
            new("SELECT decode(repeat('ff',2048),'hex')"), TestContext.Current.CancellationToken).AsTask());
        var wide = "SELECT " + string.Join(",", Enumerable.Range(0, 257).Select(index =>
            "1 AS c" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await Assert.ThrowsAsync<StudioReplyLimitException>(() => service.ExecuteAsync(scope,
            new(wide), TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Schema_discovery_shares_nonqueued_query_capacity_and_bounds_serialized_bytes()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        using var service = new StudioQueryService(Options() with { MaximumConcurrentQueries = 1, MaximumReplyBytes = 1024 });
        var schema = "studio_schema_" + Guid.NewGuid().ToString("N");
        var scope = new StudioDatabaseScope(dataSource, [schema]);
        try
        {
            await using (var setup = dataSource.CreateCommand($"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".items (id int, a text, b text, c text, d text)"))
            {
                _ = await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await WhileQueryIsBlockedAsync(dataSource, service, scope,
                () => Assert.ThrowsAsync<StudioCapacityException>(() => service.CaptureSchemaAsync(scope, TestContext.Current.CancellationToken).AsTask()));
            await Assert.ThrowsAsync<StudioReplyLimitException>(() => service.CaptureSchemaAsync(scope, TestContext.Current.CancellationToken).AsTask());
            await using (var shrink = dataSource.CreateCommand($"DROP TABLE \"{schema}\".items"))
            {
                _ = await shrink.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            Assert.True((await service.CaptureSchemaAsync(scope, TestContext.Current.CancellationToken)).Length < 1024);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            _ = await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task WhileQueryIsBlockedAsync(BlueTuskDataSource dataSource, StudioQueryService service,
        StudioDatabaseScope scope, Func<Task> assertion)
    {
        var token = TestContext.Current.CancellationToken;
        var key = BitConverter.ToInt64(Guid.NewGuid().ToByteArray());
        await using var blocker = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await blocker.BeginTransactionAsync(token);
        await using (var acquire = blocker.CreateCommand())
        {
            acquire.Transaction = transaction;
            acquire.CommandText = "SELECT pg_catalog.pg_advisory_xact_lock(@key)";
            acquire.Parameters.Add(new BlueTuskParameter<long>(key) { ParameterName = "key" });
            _ = await acquire.ExecuteScalarAsync(token);
        }
        var running = service.ExecuteAsync(scope,
            new("SELECT pg_catalog.pg_advisory_xact_lock(" + key.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"), token).AsTask();
        try { await assertion(); }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
            Assert.Equal(1, (await running).Rows);
        }
    }

    internal static StudioOptions Options() => new() { ReadPolicy = "studio-read", QueryPolicy = "studio-query" };
    internal static string ConnectionString() => Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } connection
        ? connection : throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is required for live PostgreSQL evidence.");
}

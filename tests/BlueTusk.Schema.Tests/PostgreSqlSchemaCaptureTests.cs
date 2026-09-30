using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Schema.Tests;

[Collection("Schema catalogue mutation")]
public sealed class PostgreSqlSchemaCaptureTests
{
    [Fact]
    public async Task Captures_shapes_keys_indexes_policies_and_view_definitions_and_detects_drift()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        var schema = "schema_capture_" + Guid.NewGuid().ToString("N");
        try
        {
            await ExecuteAsync(dataSource, $"""
                CREATE SCHEMA "{schema}";
                CREATE TABLE "{schema}".orders (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    tenant_id text NOT NULL, label text DEFAULT 'initial', amount numeric(12,2) NOT NULL CHECK (amount >= 0));
                CREATE INDEX orders_tenant ON "{schema}".orders (tenant_id);
                ALTER TABLE "{schema}".orders ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_scope ON "{schema}".orders USING (tenant_id = current_user);
                CREATE VIEW "{schema}".order_ids AS SELECT id FROM "{schema}".orders;
                """);
            var capture = new PostgreSqlSchemaCapture(dataSource, new() { Schemas = [schema] });
            var initial = await capture.CaptureAsync(TestContext.Current.CancellationToken);
            var repeat = await capture.CaptureAsync(TestContext.Current.CancellationToken);
            Assert.Equal(initial.Fingerprint, repeat.Fingerprint);
            var orders = Assert.Single(initial.Relations, relation => relation.Identity.Name == "orders");
            Assert.Equal(4, orders.Columns.Count);
            Assert.Equal("numeric(12,2)", Assert.Single(orders.Columns, column => column.Name == "amount").PostgreSqlType);
            Assert.Equal("a", orders.Columns[0].IdentityKind);
            Assert.True(orders.RowSecurity);
            Assert.Single(orders.Policies);
            Assert.Single(orders.Constraints, constraint => constraint.Kind == "p");
            Assert.Single(orders.Constraints, constraint => constraint.Kind == "c");
            Assert.Equal(2, orders.Indexes.Count);
            Assert.NotNull(Assert.Single(initial.Relations, relation => relation.Kind == "v").DefinitionSql);
            await ExecuteAsync(dataSource, $"ALTER TABLE \"{schema}\".orders ALTER COLUMN label TYPE varchar(80)");
            var changed = await capture.CaptureAsync(TestContext.Current.CancellationToken);
            Assert.NotEqual(initial.Fingerprint, changed.Fingerprint);
            Assert.Contains(SchemaCompatibility.Compare(initial, changed).Changes,
                change => change.Kind == SchemaChangeKind.ColumnTypeChanged && change.Member == "label");
        }
        finally
        {
            await ExecuteAsync(dataSource, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    [Fact]
    public async Task Bounds_fail_explicitly_and_do_not_poison_the_connection_pool()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        var schema = "schema_bound_" + Guid.NewGuid().ToString("N");
        try
        {
            await ExecuteAsync(dataSource, $"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".one(id int); CREATE TABLE \"{schema}\".two(id int)");
            var capture = new PostgreSqlSchemaCapture(dataSource, new() { Schemas = [schema], MaximumRelations = 1 });
            await Assert.ThrowsAsync<SchemaCaptureLimitException>(() => capture.CaptureAsync(TestContext.Current.CancellationToken).AsTask());
            var healthy = new PostgreSqlSchemaCapture(dataSource, new() { Schemas = [schema] });
            Assert.Equal(2, (await healthy.CaptureAsync(TestContext.Current.CancellationToken)).Relations.Count);
        }
        finally
        {
            await ExecuteAsync(dataSource, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    [Fact]
    public async Task Schema_filters_are_parameters_even_for_quoted_names()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        var capture = new PostgreSqlSchemaCapture(dataSource,
            new() { Schemas = ["missing'; SELECT pg_sleep(30); --"] });
        Assert.Empty((await capture.CaptureAsync(TestContext.Current.CancellationToken)).Relations);
    }

    [Fact]
    public async Task Partition_strategy_and_key_are_fingerprinted_independently_of_transient_relation_OIDs()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        var schema = "schema_partition_" + Guid.NewGuid().ToString("N");
        try
        {
            await ExecuteAsync(dataSource, $"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".root(id int, tenant text) PARTITION BY RANGE(id)");
            var capture = new PostgreSqlSchemaCapture(dataSource, new() { Schemas = [schema] });
            var range = await capture.CaptureAsync(TestContext.Current.CancellationToken);
            Assert.Contains("RANGE (id)", Assert.Single(range.Relations).DefinitionSql, StringComparison.Ordinal);
            await ExecuteAsync(dataSource, $"DROP TABLE \"{schema}\".root; CREATE TABLE \"{schema}\".root(id int, tenant text) PARTITION BY RANGE(id)");
            Assert.Equal(range.Fingerprint, (await capture.CaptureAsync(TestContext.Current.CancellationToken)).Fingerprint);
            await ExecuteAsync(dataSource, $"DROP TABLE \"{schema}\".root; CREATE TABLE \"{schema}\".root(id int, tenant text) PARTITION BY LIST(tenant)");
            var list = await capture.CaptureAsync(TestContext.Current.CancellationToken);
            Assert.NotEqual(range.Fingerprint, list.Fingerprint);
            Assert.Contains(SchemaCompatibility.Compare(range, list).Changes, value => value.Kind == SchemaChangeKind.RelationDefinitionChanged);
        }
        finally { await ExecuteAsync(dataSource, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"); }
    }

    private static async Task ExecuteAsync(BlueTuskDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static string ConnectionString() => Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } connection
        ? connection : throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is required for live PostgreSQL evidence.");
}

using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Sql.Tests;

public sealed class SqlQueryTests
{
    [Fact]
    public async Task Generated_builtin_type_shapes_preserve_finite_nonfinite_temporal_and_nullable_array_values()
    {
        await using var source = BlueTuskDataSource.Create(ConnectionString());
        await BlueTusk.Sql.Verification.TypedShapeVerification.RunAsync(source, TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task Generated_arrays_and_arbitrary_precision_numeric_preserve_shape_null_elements_and_values()
    {
        await using var source = BlueTuskDataSource.Create(ConnectionString());
        var precise = BlueTusk.TypeSystem.BlueTuskNumeric.Parse("12345678901234567890123456789012345678901234567890.000000000001");
        var arguments = new global::BlueTusk.Sql.Tests.Generated.TypedCollections.Arguments([int.MinValue, 0, int.MaxValue], ["a,b", null, "NULL", "🐘"],
            [1, null, -1], precise, null, new int[,] { { 1, 2 }, { 3, 4 } });
        await global::BlueTusk.Sql.Tests.Generated.TypedCollections.Definition.ValidateAsync(source, arguments, TestContext.Current.CancellationToken);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var count = 0;
        await foreach (var row in global::BlueTusk.Sql.Tests.Generated.TypedCollections.Definition.ReadAsync(connection, arguments, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(arguments.numbers, row.Numbers);
            Assert.Equal(arguments.texts, row.Texts);
            Assert.Equal(arguments.nullableNumbers, row.NullableNumbers);
            Assert.Equal(precise, row.Precise);
            Assert.Null(row.WholeNullable);
            Assert.Equal(2, row.Matrix.GetLength(0));
            Assert.Equal(2, row.Matrix.GetLength(1));
            Assert.Equal(arguments.matrix.Cast<int>(), row.Matrix.Cast<int>());
            count++;
        }
        Assert.Equal(1, count);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in global::BlueTusk.Sql.Tests.Generated.TypedCollections.Definition.ReadAsync(connection, arguments with { numbers = null! }, cancellationToken: TestContext.Current.CancellationToken)) { }
        });
    }

    [Fact]
    public async Task Generated_query_binds_nullable_values_and_streams_typed_rows()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        await global::BlueTusk.Sql.Tests.Generated.TypedRows.Definition.ValidateAsync(dataSource, new(3, null), TestContext.Current.CancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var rows = new List<global::BlueTusk.Sql.Tests.Generated.TypedRows.Row>();
        await foreach (var row in global::BlueTusk.Sql.Tests.Generated.TypedRows.Definition.ReadAsync(connection, new(3, null), cancellationToken: TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal(1, rows[0].Id);
        Assert.Null(rows[0].Label);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task Result_overflow_fails_and_connection_remains_usable()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqlResultLimitException>(async () =>
        {
            await foreach (var row in global::BlueTusk.Sql.Tests.Generated.TypedRows.Definition.ReadAsync(connection, new(4, "label"), cancellationToken: TestContext.Current.CancellationToken))
            {
                Assert.Equal("label", row.Label);
            }
        });
        await using var healthy = connection.CreateCommand();
        healthy.CommandText = "SELECT 42";
        Assert.Equal(42, await healthy.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Parameterless_large_result_stops_at_one_overflow_row_on_the_server()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TEMP SEQUENCE bluetusk_sql_row_budget_seq";
            await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var query = new SqlQuery<int, long>("bounded-rows",
            "SELECT nextval('pg_temp.bluetusk_sql_row_budget_seq') AS value FROM generate_series(1, 1000000)",
            [new("value", "int8", false)], static (_, _) => { }, static reader => reader.GetInt64(0),
            maximumRows: 2, commandTimeoutSeconds: 10);
        var returned = new List<long>();
        await Assert.ThrowsAsync<SqlResultLimitException>(async () =>
        {
            await foreach (var row in query.ReadAsync(connection, 0, cancellationToken: TestContext.Current.CancellationToken))
            {
                returned.Add(row);
            }
        });

        Assert.Equal(new long[] { 1, 2 }, returned);
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT last_value FROM pg_temp.bluetusk_sql_row_budget_seq";
        Assert.Equal(3L, Assert.IsType<long>(await probe.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Bounded_wrapper_preserves_cte_order_limit_offset_parameters_and_terminal_comments()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var query = new SqlQuery<int, int>("wrapped-query",
            "WITH valueset(value) AS (VALUES (1), (2), (3), (4)) " +
            "SELECT value FROM valueset WHERE value <= $1::int4 ORDER BY value DESC LIMIT 2 OFFSET 1; -- terminal",
            [new("value", "int4", false)],
            static (command, maximum) => command.Parameters.Add(new BlueTuskParameter<int>(maximum)),
            static reader => reader.GetInt32(0), maximumRows: 3);
        await query.ValidateAsync(dataSource, 4, TestContext.Current.CancellationToken);
        var returned = new List<int>();
        await foreach (var row in query.ReadAsync(connection, 4, cancellationToken: TestContext.Current.CancellationToken))
        {
            returned.Add(row);
        }
        Assert.Collection(returned, value => Assert.Equal(3, value), value => Assert.Equal(2, value));
    }

    [Fact]
    public async Task Bounded_wrapper_preserves_for_update_in_a_callers_transaction()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var setup = connection.CreateCommand())
        {
            setup.Transaction = transaction;
            setup.CommandText = "CREATE TEMP TABLE bluetusk_sql_lock_rows(value int4) ON COMMIT DROP; " +
                "INSERT INTO bluetusk_sql_lock_rows(value) VALUES (2), (1)";
            await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var query = new SqlQuery<int, int>("locking-read",
            "SELECT value FROM pg_temp.bluetusk_sql_lock_rows ORDER BY value FOR UPDATE",
            [new("value", "int4", false)], static (_, _) => { }, static reader => reader.GetInt32(0), maximumRows: 2);
        var returned = new List<int>();
        await foreach (var row in query.ReadAsync(connection, 0, transaction, TestContext.Current.CancellationToken))
        {
            returned.Add(row);
        }
        Assert.Collection(returned, value => Assert.Equal(1, value), value => Assert.Equal(2, value));
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Authoritative_validation_rejects_column_name_and_type_drift_without_reading_rows()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        var query = new SqlQuery<int, int>("invalid", "SELECT 1::int4 AS actual", [new("expected", "int4", false)],
            static (_, _) => { }, static reader => reader.GetInt32(0));
        await Assert.ThrowsAsync<SqlContractMismatchException>(() => query.ValidateAsync(dataSource, 0, TestContext.Current.CancellationToken).AsTask());
        var typeMismatch = new SqlQuery<int, int>("invalid-type", "SELECT 1::int4 AS actual", [new("actual", "int8", false)],
            static (_, _) => { }, static reader => reader.GetInt32(0));
        await Assert.ThrowsAsync<SqlContractMismatchException>(() => typeMismatch.ValidateAsync(dataSource, 0, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Required_nulls_fail_before_materialization_and_early_disposal_releases_reader()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var query = new SqlQuery<int, int>("null", "SELECT NULL::int4 AS value", [new("value", "int4", false)],
            static (_, _) => { }, static reader => reader.GetInt32(0));
        await Assert.ThrowsAsync<SqlContractMismatchException>(async () =>
        {
            await foreach (var _ in query.ReadAsync(connection, 0, cancellationToken: TestContext.Current.CancellationToken)) { }
        });
        await foreach (var row in global::BlueTusk.Sql.Tests.Generated.TypedRows.Definition.ReadAsync(connection, new(3, "early"), cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(1, row.Id);
            break;
        }
        await using var healthy = connection.CreateCommand();
        healthy.CommandText = "SELECT 42";
        Assert.Equal(42, await healthy.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Uses_callers_transaction_and_does_not_commit_or_dispose_it()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await foreach (var row in global::BlueTusk.Sql.Tests.Generated.TypedRows.Definition.ReadAsync(connection, new(1, "tx"), transaction, TestContext.Current.CancellationToken))
        {
            Assert.Equal("tx", row.Label);
        }

        Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Contract_fingerprint_includes_result_types_and_execution_bounds()
    {
        static SqlQuery<int, int> Query(int maximumRows, string type) => new("query", "SELECT 1 AS id",
            [new("id", type, false)], static (_, _) => { }, static reader => reader.GetInt32(0), maximumRows);
        Assert.Equal(Query(10, "int4").Fingerprint, Query(10, "int4").Fingerprint);
        Assert.NotEqual(Query(10, "int4").Fingerprint, Query(11, "int4").Fingerprint);
        Assert.NotEqual(Query(10, "int4").Fingerprint, Query(10, "int8").Fingerprint);
    }

    [Fact]
    public async Task Cancellation_is_observed_without_disposing_callers_connection()
    {
        await using var dataSource = BlueTuskDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in global::BlueTusk.Sql.Tests.Generated.TypedRows.Definition.ReadAsync(connection, new(1, "cancel"), cancellationToken: source.Token)) { }
        });
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    private static string ConnectionString() => Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } connection
        ? connection : throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is required for live PostgreSQL evidence.");
}

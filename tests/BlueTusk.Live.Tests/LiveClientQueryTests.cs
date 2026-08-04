using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Live.AspNetCore;
using BlueTusk.Live.Testing;
using Xunit.Sdk;

namespace BlueTusk.Live.Tests;

public sealed class LiveClientQueryTests
{
    [Fact]
    public void Compiler_accepts_capability_bounded_sql_and_remote_linq()
    {
        using var dataSource = new QueryDataSource();
        var policy = Policy(allowSql: true);
        var parameters = new[]
        {
            new LiveQueryParameter("tenant", typeof(string)),
            new LiveQueryParameter("minimum", typeof(decimal)),
        };
        var sql = LiveClientQueryDefinition.CreateSql(
            "client:orders",
            "v1",
            """
            SELECT id, tenant_id, total
            FROM sales.orders
            WHERE tenant_id = @tenant AND total >= @minimum
            ORDER BY id
            LIMIT 25
            """,
            parameters,
            ["id"],
            25);
        var linq = LiveClientQueryDefinition.CreateLinq(
            "client:orders",
            "v1",
            new LiveClientLinqQuery(
                "sales",
                "orders",
                ["id", "tenant_id", "total"],
                [
                    new LiveClientFilter(
                        "tenant_id",
                        LiveClientFilterOperator.Equal,
                        "tenant"),
                    new LiveClientFilter(
                        "total",
                        LiveClientFilterOperator.GreaterThanOrEqual,
                        "minimum"),
                ],
                [new LiveClientOrdering("id")]),
            parameters,
            ["id"],
            25);

        var sqlPlan = LiveClientQueryCompiler.Compile(dataSource, policy, sql);
        var linqPlan = LiveClientQueryCompiler.Compile(dataSource, policy, linq);

        Assert.Equal(
            ["sales.order_lines", "sales.orders"],
            sqlPlan.Dependencies.Select(static dependency => dependency.ToString()));
        Assert.Equal("sales.orders", Assert.Single(linqPlan.Dependencies).ToString());
        Assert.True(sqlPlan.Capabilities.HasFlag(LiveQueryCapabilities.TenantFilter));
        Assert.True(linqPlan.Capabilities.HasFlag(LiveQueryCapabilities.SingleTable));
        Assert.NotEqual(sqlPlan.Fingerprint, linqPlan.Fingerprint);
        Assert.Equal(
            "acme",
            linqPlan.Bind(new Dictionary<string, object?>
            {
                ["tenant"] = "acme",
                ["minimum"] = 10m,
            }).Get<string>("tenant"));
    }

    [Fact]
    public void Compiler_rejects_policy_shape_and_side_effect_escapes()
    {
        using var dataSource = new QueryDataSource();
        var parameter = new LiveQueryParameter("tenant", typeof(string));
        Assert.Throws<LiveClientQueryRegistrationException>(() =>
            LiveClientQueryCompiler.Compile(
                dataSource,
                Policy(allowSql: false),
                Sql("SELECT id FROM sales.orders ORDER BY id", parameter)));
        var missingDedicatedRole = new LiveClientQueryPolicy(
            "orders-read",
            "v1",
            "primary",
            LiveClientSecurityMode.DatabaseRowLevelSecurity,
            [new LiveClientRelation("sales", "orders", ["id"])],
            allowSql: true);
        Assert.Throws<LiveClientQueryRegistrationException>(() =>
            LiveClientQueryCompiler.Compile(
                dataSource,
                missingDedicatedRole,
                Sql("SELECT id FROM sales.orders ORDER BY id", parameter)));

        foreach (var sql in new[]
                 {
                     "DELETE FROM sales.orders RETURNING id",
                     "WITH changed AS (UPDATE sales.orders SET total = 0 RETURNING id) SELECT id FROM changed",
                     "SELECT pg_sleep(30), id FROM sales.orders",
                     "SELECT id FROM sales.orders; SELECT id FROM sales.orders",
                     "SELECT id FROM sales.orders -- hidden",
                 })
        {
            Assert.Throws<LiveClientQueryRegistrationException>(() =>
                LiveClientQueryCompiler.Compile(
                    dataSource,
                    Policy(allowSql: true),
                    Sql(sql, parameter)));
        }

        var unallowedColumn = LiveClientQueryDefinition.CreateLinq(
            "client:orders",
            "v1",
            new LiveClientLinqQuery(
                "sales",
                "orders",
                ["id", "secret"],
                [new LiveClientFilter("tenant_id", LiveClientFilterOperator.Equal, "tenant")],
                [new LiveClientOrdering("id")]),
            [parameter],
            ["id"],
            25);
        Assert.Throws<LiveClientQueryRegistrationException>(() =>
            LiveClientQueryCompiler.Compile(
                dataSource,
                Policy(allowSql: true),
                unallowedColumn));

        var unstableKey = LiveClientQueryDefinition.CreateLinq(
            "client:orders",
            "v1",
            new LiveClientLinqQuery(
                "sales",
                "orders",
                ["id", "tenant_id"],
                [new LiveClientFilter("tenant_id", LiveClientFilterOperator.Equal, "tenant")],
                [new LiveClientOrdering("tenant_id")]),
            [parameter],
            ["id"],
            25);
        Assert.Throws<LiveClientQueryRegistrationException>(() =>
            LiveClientQueryCompiler.Compile(
                dataSource,
                Policy(allowSql: true),
                unstableKey));
    }

    [Fact]
    public async Task Transport_resolver_authorizes_executes_read_only_and_shares_by_security_scope()
    {
        using var dataSource = new QueryDataSource(
            (1, "acme", 42.5m),
            (2, "acme", 50m));
        var invalidations = new InMemoryLiveInvalidationLog();
        var replay = new InMemoryLiveReplayStore();
        await using var registry = new LiveSharedSubscriptionRegistry();
        var authorizer = new Authorizer(
            new LiveClientQueryGrant(
                dataSource,
                Policy(allowSql: true),
                new LiveSecurityScope("tenant:acme:user:17", "orders-policy-v3")));
        var resolver = new LiveClientQueryTransportResolver(
            authorizer,
            invalidations,
            replay,
            registry);
        using var document = JsonDocument.Parse(
            """
            {
              "language": "linq",
              "linq": {
                "schema": "sales",
                "table": "orders",
                "columns": ["id", "tenant_id", "total"],
                "filters": [
                  { "column": "tenant_id", "operator": "Equal", "parameter": "tenant" },
                  { "column": "total", "operator": "GreaterThanOrEqual", "parameter": "minimum" }
                ],
                "orderings": [
                  { "column": "id", "direction": "Ascending" }
                ]
              },
              "keyColumns": ["id"],
              "maximumResultCount": 25,
              "parameters": {
                "minimum": { "type": "decimal", "value": 10 },
                "tenant": { "type": "string", "value": "acme" }
              }
            }
            """);
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "17")],
                "test"));

        var first = await resolver.ResolveAsync(
            "orders-read",
            document.RootElement,
            principal,
            TestContext.Current.CancellationToken);
        var second = await resolver.ResolveAsync(
            "orders-read",
            document.RootElement,
            principal,
            TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.True(first.Status.IsStarted);
        Assert.Equal(1, registry.Count);
        Assert.Equal(2, authorizer.Calls);
        Assert.Contains(
            dataSource.Commands,
            command => string.Equals(
                command,
                "SET TRANSACTION READ ONLY",
                StringComparison.Ordinal));
        Assert.Contains(
            dataSource.Commands,
            command => command.StartsWith(
                "SET LOCAL statement_timeout = ",
                StringComparison.Ordinal));
        Assert.Contains(
            dataSource.Commands,
            command =>
                command.Contains("FROM \"sales\".\"orders\"", StringComparison.Ordinal) &&
                command.Contains("LIMIT 25", StringComparison.Ordinal));
        Assert.Equal("acme", dataSource.Parameters["tenant"]);
        Assert.Equal(10m, dataSource.Parameters["minimum"]);

        var connection = await first.ConnectAsync(
            0,
            TestContext.Current.CancellationToken);
        Assert.Equal(LiveSubscriptionConnectStatus.Connected, connection.Status);
        var initial = Assert.Single(connection.Connection!.Replay);
        using var payload = JsonDocument.Parse(initial.Payload);
        Assert.Equal(
            "InitialResult",
            payload.RootElement.GetProperty("kind").GetString());
        Assert.Equal(
            2,
            payload.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(
            JsonValueKind.String,
            payload.RootElement.GetProperty("order")[0].ValueKind);
        await connection.Connection.DisposeAsync();
    }

    [Fact]
    public async Task Transport_resolver_rejects_unknown_fields_and_denied_capabilities()
    {
        using var dataSource = new QueryDataSource();
        await using var registry = new LiveSharedSubscriptionRegistry();
        var resolver = new LiveClientQueryTransportResolver(
            new Authorizer(null),
            new InMemoryLiveInvalidationLog(),
            new InMemoryLiveReplayStore(),
            registry);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"));
        using var unknown = JsonDocument.Parse(
            """
            {
              "language": "sql",
              "sql": "SELECT id FROM sales.orders ORDER BY id",
              "keyColumns": ["id"],
              "maximumResultCount": 25,
              "parameters": {},
              "bypassPolicy": true
            }
            """);
        await Assert.ThrowsAsync<LiveTransportRequestException>(() =>
            resolver.ResolveAsync(
                "orders-read",
                unknown.RootElement,
                principal,
                TestContext.Current.CancellationToken).AsTask());

        using var valid = JsonDocument.Parse(
            """
            {
              "language": "sql",
              "sql": "SELECT id FROM sales.orders ORDER BY id",
              "keyColumns": ["id"],
              "maximumResultCount": 25,
              "parameters": {}
            }
            """);
        await Assert.ThrowsAsync<LiveTransportAuthorizationException>(() =>
            resolver.ResolveAsync(
                "orders-read",
                valid.RootElement,
                principal,
                TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task PostgreSQL_client_query_is_parameterized_bounded_and_transaction_read_only()
    {
        var schema = "bluetusk_client_query_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        try
        {
            await ExecuteAsync(dataSource, $"CREATE SCHEMA \"{schema}\"");
            await ExecuteAsync(
                dataSource,
                $"""
                CREATE TABLE "{schema}".orders (
                    id integer PRIMARY KEY,
                    tenant_id text NOT NULL,
                    total numeric NOT NULL)
                """);
            await ExecuteAsync(
                dataSource,
                $"""
                INSERT INTO "{schema}".orders (id, tenant_id, total)
                VALUES (1, 'acme', 42.5), (2, 'other', 99)
                """);
            await ExecuteAsync(
                dataSource,
                $"""
                CREATE FUNCTION "{schema}".attempt_write()
                RETURNS integer
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    INSERT INTO "{schema}".orders (id, tenant_id, total)
                    VALUES (99, 'acme', 1);
                    RETURN 99;
                END
                $function$
                """);
            var policy = new LiveClientQueryPolicy(
                "orders-read",
                "v1",
                "primary",
                LiveClientSecurityMode.DatabaseRowLevelSecurity |
                    LiveClientSecurityMode.DedicatedReadOnlyRole,
                [new LiveClientRelation(schema, "orders", ["id", "tenant_id", "total"])],
                allowSql: true,
                maximumResultCount: 10);
            var definition = LiveClientQueryDefinition.CreateSql(
                "client:orders",
                "v1",
                $"""
                SELECT id, tenant_id, total
                FROM "{schema}".orders
                WHERE tenant_id = @tenant
                ORDER BY id
                """,
                [new LiveQueryParameter("tenant", typeof(string))],
                ["id"],
                10);
            var plan = LiveClientQueryCompiler.Compile(
                dataSource,
                policy,
                definition);
            var rows = await plan.ExecuteAsync(
                new LiveQueryExecutionContext(
                    plan.Bind(new Dictionary<string, object?> { ["tenant"] = "acme" }),
                    new LiveSecurityScope("tenant:acme", "orders-v1")),
                TestContext.Current.CancellationToken);

            var row = Assert.Single(rows);
            Assert.Equal(1, row.Get<int>("id"));
            Assert.Equal("acme", row.Get<string>("tenant_id"));

            var sideEffect = LiveClientQueryCompiler.Compile(
                dataSource,
                policy,
                LiveClientQueryDefinition.CreateSql(
                    "client:side-effect",
                    "v1",
                    $"""SELECT "{schema}".attempt_write() AS id ORDER BY id""",
                    [],
                    ["id"],
                    10));
            await Assert.ThrowsAsync<LiveClientQueryExecutionException>(() =>
                sideEffect.ExecuteAsync(
                    new LiveQueryExecutionContext(
                        sideEffect.Bind(new Dictionary<string, object?>()),
                        new LiveSecurityScope("tenant:acme", "orders-v1")),
                    TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            await ExecuteAsync(
                dataSource,
                $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    private static LiveClientQueryDefinition Sql(
        string sql,
        LiveQueryParameter parameter) =>
        LiveClientQueryDefinition.CreateSql(
            "client:orders",
            "v1",
            sql,
            [parameter],
            ["id"],
            25);

    private static LiveClientQueryPolicy Policy(bool allowSql) =>
        new(
            "orders-read",
            "v3",
            "primary",
            LiveClientSecurityMode.DatabaseRowLevelSecurity |
                LiveClientSecurityMode.DedicatedReadOnlyRole,
            [
                new LiveClientRelation(
                    "sales",
                    "orders",
                    ["id", "tenant_id", "total"]),
                new LiveClientRelation(
                    "sales",
                    "order_lines",
                    ["id", "order_id", "total"]),
            ],
            allowSql,
            maximumResultCount: 100,
            statementTimeout: TimeSpan.FromSeconds(2),
            lockTimeout: TimeSpan.FromMilliseconds(250));

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } value
            ? value
            : throw SkipException.ForSkip(
                "Set BLUETUSK_TEST_CONNECTION_STRING to run live client-query acceptance.");

    private static async Task ExecuteAsync(
        DbDataSource dataSource,
        string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed class Authorizer(LiveClientQueryGrant? grant)
        : ILiveClientQueryAuthorizer
    {
        public int Calls { get; private set; }

        public ValueTask<LiveClientQueryGrant?> AuthorizeAsync(
            string capability,
            LiveClientQueryDefinition definition,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(grant);
        }
    }

    private sealed class QueryDataSource : DbDataSource
    {
        private readonly DataTable _rows = new();
        private readonly List<string> _commands = [];
        private readonly Dictionary<string, object?> _parameters =
            new(StringComparer.Ordinal);

        public QueryDataSource(params (int Id, string Tenant, decimal Total)[] rows)
        {
            _rows.Columns.Add("id", typeof(int));
            _rows.Columns.Add("tenant_id", typeof(string));
            _rows.Columns.Add("total", typeof(decimal));
            foreach (var row in rows)
            {
                _rows.Rows.Add(row.Id, row.Tenant, row.Total);
            }
        }

        public override string ConnectionString => "Host=fake";

        public IReadOnlyList<string> Commands => _commands;

        public Dictionary<string, object?> Parameters => _parameters;

        protected override DbConnection CreateDbConnection() =>
            new QueryConnection(this);

        public void Record(QueryCommand command)
        {
            _commands.Add(command.CommandText);
            foreach (DbParameter parameter in command.Parameters)
            {
                _parameters[parameter.ParameterName] = parameter.Value is DBNull
                    ? null
                    : parameter.Value;
            }
        }

        public DataTableReader CreateReader() => _rows.CreateDataReader();
    }

    private sealed class QueryConnection(QueryDataSource owner) : DbConnection
    {
        private ConnectionState _state;

        [AllowNull]
        public override string ConnectionString { get; set; } = "Host=fake";

        public override string Database => "fake";

        public override string DataSource => "fake";

        public override string ServerVersion => "19";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            new QueryTransaction(this, isolationLevel);

        protected override DbCommand CreateDbCommand() =>
            new QueryCommand(owner) { Connection = this };
    }

    private sealed class QueryTransaction(
        DbConnection connection,
        IsolationLevel isolationLevel) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => isolationLevel;

        protected override DbConnection DbConnection => connection;

        public override void Commit()
        {
        }

        public override void Rollback()
        {
        }
    }

    private sealed class QueryCommand(QueryDataSource owner) : DbCommand
    {
        private readonly QueryParameterCollection _parameters = new();

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; } = CommandType.Text;

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery()
        {
            owner.Record(this);
            return 0;
        }

        public override object? ExecuteScalar()
        {
            owner.Record(this);
            return null;
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => new QueryParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            owner.Record(this);
            return owner.CreateReader();
        }
    }

    private sealed class QueryParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; } =
            ParameterDirection.Input;

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        public override int Size { get; set; }

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override bool SourceColumnNullMapping { get; set; }

        public override object? Value { get; set; }

        public override void ResetDbType()
        {
        }
    }

    private sealed class QueryParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _parameters = [];

        public override int Count => _parameters.Count;

        public override object SyncRoot => ((ICollection)_parameters).SyncRoot;

        public override int Add(object value)
        {
            _parameters.Add((DbParameter)value);
            return _parameters.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
            {
                _ = Add(value!);
            }
        }

        public override void Clear() => _parameters.Clear();

        public override bool Contains(object value) =>
            _parameters.Contains((DbParameter)value);

        public override bool Contains(string value) =>
            IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index) =>
            ((ICollection)_parameters).CopyTo(array, index);

        public override IEnumerator GetEnumerator() =>
            _parameters.GetEnumerator();

        public override int IndexOf(object value) =>
            _parameters.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName) =>
            _parameters.FindIndex(parameter =>
                string.Equals(
                    parameter.ParameterName,
                    parameterName,
                    StringComparison.Ordinal));

        public override void Insert(int index, object value) =>
            _parameters.Insert(index, (DbParameter)value);

        public override void Remove(object value) =>
            _parameters.Remove((DbParameter)value);

        public override void RemoveAt(int index) =>
            _parameters.RemoveAt(index);

        public override void RemoveAt(string parameterName)
        {
            var index = IndexOf(parameterName);
            if (index >= 0)
            {
                RemoveAt(index);
            }
        }

        protected override DbParameter GetParameter(int index) =>
            _parameters[index];

        protected override DbParameter GetParameter(string parameterName) =>
            _parameters[IndexOf(parameterName)];

        protected override void SetParameter(int index, DbParameter value) =>
            _parameters[index] = value;

        protected override void SetParameter(
            string parameterName,
            DbParameter value)
        {
            var index = IndexOf(parameterName);
            if (index < 0)
            {
                _parameters.Add(value);
            }
            else
            {
                _parameters[index] = value;
            }
        }
    }
}

using System.Globalization;
using BlueTusk.Benchmarks.ExpansionCapacity;
using BlueTusk.Data;

namespace BlueTusk.Sql.LoadHarness;

internal static class Program
{
    private static Task<int> Main(string[] args) =>
        CapacityHost.RunAsync(args, "Sql", static (context, cancellationToken) => SqlWorkload.CreateAsync(context, cancellationToken));
}

internal sealed record OrderRow(long Id, int Tenant, string Status, decimal Amount, long Version, long Checksum);

internal sealed record PageRow(long Id, int Tenant, DateTimeOffset CreatedAt, string Status, decimal Amount, long Version,
    long Checksum, string Note);

internal sealed record StatusTotal(string Status, long Orders, decimal Amount);

// Sustained Sql hot path: SqlQuery<TArguments,TResult>.ReadAsync over caller-owned BlueTusk.Data
// connections, with the same parameter binding and typed projection the source generator emits,
// while fixed-cardinality DML keeps the rows changing. Every row is checked for tenant, order and
// an in-row checksum that every update maintains.
internal sealed class SqlWorkload : CapacityWorkload
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly BlueTuskDataSource _source;
    private readonly string _schema;
    private readonly int _tenants;
    private readonly int _orders;
    private readonly int _noteBytes;
    private readonly int _pageRows;
    private readonly int _pointWorkers;
    private readonly int _pageWorkers;
    private readonly int _aggregateWorkers;
    private readonly int _writeWorkers;
    private readonly SqlQuery<(int Tenant, long Id), OrderRow> _point;
    private readonly SqlQuery<(int Tenant, DateTimeOffset From), PageRow> _page;
    private readonly SqlQuery<int, StatusTotal> _aggregate;
    private long _verifiedRows;
    private long _applicationUpdates;

    private SqlWorkload(CapacityContext context, BlueTuskDataSource source)
    {
        _source = source;
        _schema = "sql_capacity_" + Guid.NewGuid().ToString("N")[..16];
        _tenants = context.IntParameter("tenants");
        _orders = context.IntParameter("ordersPerTenant");
        _noteBytes = context.IntParameter("noteBytes");
        _pageRows = context.IntParameter("pageRows");
        _pointWorkers = context.Schedule("point-lookup").Workers;
        _pageWorkers = context.Schedule("range-page").Workers;
        _aggregateWorkers = context.Schedule("aggregate").Workers;
        _writeWorkers = context.Schedule("application-write").Workers;
        _point = new("capacity.point-lookup",
            $"SELECT id, tenant, status, amount, version, checksum FROM \"{_schema}\".orders WHERE tenant = $1::int4 AND id = $2::int8",
            [new("id", "int8", false), new("tenant", "int4", false), new("status", "text", false), new("amount", "numeric", false),
                new("version", "int8", false), new("checksum", "int8", false)],
            static (command, arguments) =>
            {
                command.Parameters.Add(new BlueTuskParameter<int>(arguments.Tenant) { PostgreSqlTypeOid = 23u });
                command.Parameters.Add(new BlueTuskParameter<long>(arguments.Id) { PostgreSqlTypeOid = 20u });
            },
            static reader => new OrderRow(reader.GetFieldValue<long>(0), reader.GetFieldValue<int>(1), reader.GetFieldValue<string>(2),
                reader.GetFieldValue<decimal>(3), reader.GetFieldValue<long>(4), reader.GetFieldValue<long>(5)),
            maximumRows: 1);
        _page = new("capacity.range-page", $"""
            SELECT id, tenant, created_at, status, amount, version, checksum, note FROM "{_schema}".orders
            WHERE tenant = $1::int4 AND created_at >= $2::timestamptz ORDER BY created_at, id LIMIT {_pageRows}
            """,
            [new("id", "int8", false), new("tenant", "int4", false), new("created_at", "timestamptz", false), new("status", "text", false),
                new("amount", "numeric", false), new("version", "int8", false), new("checksum", "int8", false), new("note", "text", false)],
            static (command, arguments) =>
            {
                command.Parameters.Add(new BlueTuskParameter<int>(arguments.Tenant) { PostgreSqlTypeOid = 23u });
                command.Parameters.Add(new BlueTuskParameter<DateTimeOffset>(arguments.From) { PostgreSqlTypeOid = 1184u });
            },
            static reader => new PageRow(reader.GetFieldValue<long>(0), reader.GetFieldValue<int>(1), reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetFieldValue<string>(3), reader.GetFieldValue<decimal>(4), reader.GetFieldValue<long>(5),
                reader.GetFieldValue<long>(6), reader.GetFieldValue<string>(7)),
            maximumRows: _pageRows);
        _aggregate = new("capacity.aggregate", $"""
            SELECT status, count(*)::int8 AS orders, sum(amount) AS amount FROM "{_schema}".orders
            WHERE tenant = $1::int4 GROUP BY status ORDER BY status
            """,
            [new("status", "text", false), new("orders", "int8", false), new("amount", "numeric", false)],
            static (command, tenant) => command.Parameters.Add(new BlueTuskParameter<int>(tenant) { PostgreSqlTypeOid = 23u }),
            static reader => new StatusTotal(reader.GetFieldValue<string>(0), reader.GetFieldValue<long>(1), reader.GetFieldValue<decimal>(2)),
            maximumRows: 8);
    }

    public static async Task<CapacityWorkload> CreateAsync(CapacityContext context, CancellationToken cancellationToken)
    {
        var settings = new BlueTuskConnectionStringBuilder(context.ConnectionString)
        {
            MaximumPoolSize = context.IntParameter("maximumPoolSize"),
            ApplicationName = "bluetusk-sql-capacity",
        };
        var workload = new SqlWorkload(context, BlueTuskDataSource.Create(settings.ConnectionString));
        try
        {
            await using (var command = workload._source.CreateCommand(string.Create(CultureInfo.InvariantCulture, $"""
                CREATE SCHEMA "{workload._schema}";
                CREATE TABLE "{workload._schema}".orders (
                    tenant integer NOT NULL, id bigint NOT NULL, created_at timestamptz NOT NULL, status text NOT NULL,
                    amount numeric(12,2) NOT NULL, version bigint NOT NULL, checksum bigint NOT NULL, note text NOT NULL,
                    PRIMARY KEY (tenant, id));
                INSERT INTO "{workload._schema}".orders
                    SELECT t, i, timestamptz '2026-01-01 00:00:00+00' + i * interval '1 second',
                        (ARRAY['closed','new','paid','shipped'])[i % 4 + 1], (i % 1000) + 0.25, 1, i * 31 + 7 + t,
                        left(repeat(md5(i::text), 8), {workload._noteBytes})
                    FROM generate_series(0, {workload._tenants - 1}) AS t, generate_series(1, {workload._orders}) AS i;
                CREATE INDEX orders_tenant_created ON "{workload._schema}".orders (tenant, created_at, id);
                ANALYZE "{workload._schema}".orders;
                """)))
            {
                _ = await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return workload;
        }
        catch
        {
            await workload.DisposeAsync();
            throw;
        }
    }

    public override IReadOnlyList<string> OwnedSchemas => [_schema];

    public override IReadOnlyList<ScheduledOperation> Operations =>
    [
        new("point-lookup", PointLookupAsync),
        new("range-page", RangePageAsync),
        new("aggregate", AggregateAsync),
        new("application-write", WriteAsync),
    ];

    private static bool Consistent(long id, int tenant, long version, long checksum) => checksum == id * 31 + version * 7 + tenant;

    private async ValueTask<OperationOutcome> PointLookupAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var ordinal = slot * _pointWorkers + worker;
        var tenant = (int)(ordinal % _tenants);
        var id = ordinal * 7_919 % _orders + 1;
        var rows = 0;
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await foreach (var row in _point.ReadAsync(connection, (tenant, id), cancellationToken: cancellationToken))
        {
            CapacityHost.Check(row.Id == id && row.Tenant == tenant && Consistent(row.Id, row.Tenant, row.Version, row.Checksum) &&
                row.Version >= 1, "A typed point lookup returned a foreign or torn row.");
            rows++;
        }

        CapacityHost.Check(rows == 1, "A typed point lookup did not return exactly its row.");
        Interlocked.Increment(ref _verifiedRows);
        return OperationOutcome.Accepted;
    }

    private async ValueTask<OperationOutcome> RangePageAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var ordinal = slot * _pageWorkers + worker;
        var tenant = (int)(ordinal % _tenants);
        var first = ordinal * 104_729 % (_orders - _pageRows + 1) + 1;
        var from = Epoch.AddSeconds(first);
        var rows = 0;
        PageRow? previous = null;
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await foreach (var row in _page.ReadAsync(connection, (tenant, from), cancellationToken: cancellationToken))
        {
            CapacityHost.Check(row.Tenant == tenant && row.Id == first + rows && row.CreatedAt == Epoch.AddSeconds(row.Id) &&
                Consistent(row.Id, row.Tenant, row.Version, row.Checksum) && row.Note.Length == _noteBytes &&
                (previous is null || previous.CreatedAt < row.CreatedAt), "A typed page returned a foreign, unordered or torn row.");
            previous = row;
            rows++;
        }

        CapacityHost.Check(rows == _pageRows, "A typed page did not return its full bounded page.");
        Interlocked.Add(ref _verifiedRows, rows);
        return OperationOutcome.Accepted;
    }

    private async ValueTask<OperationOutcome> AggregateAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var tenant = (int)((slot * _aggregateWorkers + worker) % _tenants);
        long orders = 0;
        var groups = 0;
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await foreach (var row in _aggregate.ReadAsync(connection, tenant, cancellationToken: cancellationToken))
        {
            CapacityHost.Check(row.Orders > 0 && row.Amount > 0, "A typed aggregate returned an empty group.");
            orders += row.Orders;
            groups++;
        }

        CapacityHost.Check(groups == 4 && orders == _orders, "A typed aggregate lost or invented rows.");
        Interlocked.Add(ref _verifiedRows, groups);
        return OperationOutcome.Accepted;
    }

    private async ValueTask<OperationOutcome> WriteAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var ordinal = slot * _writeWorkers + worker;
        var tenant = ordinal % _tenants;
        var id = ordinal * 3_571 % _orders + 1;
        await using var command = _source.CreateCommand(string.Create(CultureInfo.InvariantCulture, $"""
            UPDATE "{_schema}".orders SET version = version + 1, checksum = id * 31 + (version + 1) * 7 + tenant,
                amount = amount + 1 WHERE tenant = {tenant} AND id = {id}
            """));
        CapacityHost.Check(await command.ExecuteNonQueryAsync(cancellationToken) == 1, "Fixed-cardinality DML did not update exactly one row.");
        Interlocked.Increment(ref _applicationUpdates);
        return OperationOutcome.Accepted;
    }

    public override async Task DrainAndVerifyAsync(CapacityRecorder recorder, CancellationToken cancellationToken)
    {
        recorder.Counter("verifiedRows", Interlocked.Read(ref _verifiedRows));
        recorder.Counter("applicationUpdates", Interlocked.Read(ref _applicationUpdates));

        var validated = true;
        try
        {
            await _point.ValidateAsync(_source, (0, 1), cancellationToken);
            await _page.ValidateAsync(_source, (0, Epoch), cancellationToken);
            await _aggregate.ValidateAsync(_source, 0, cancellationToken);
        }
        catch (SqlContractMismatchException) { validated = false; }
        recorder.Invariant("contracts-validate", validated, "Every measured contract validates against the live catalogue.");

        var limited = false;
        var tooSmall = new SqlQuery<int, long>("capacity.row-limit", $"SELECT id FROM \"{_schema}\".orders WHERE tenant = $1::int4 ORDER BY id",
            [new("id", "int8", false)], static (command, tenant) => command.Parameters.Add(new BlueTuskParameter<int>(tenant) { PostgreSqlTypeOid = 23u }),
            static reader => reader.GetFieldValue<long>(0), maximumRows: 2);
        try { _ = await DrainAsync(tooSmall, 0, cancellationToken); }
        catch (SqlResultLimitException) { limited = true; }
        recorder.Invariant("row-limit-fails-closed", limited, "A result beyond maximumRows must fail instead of truncating.");

        var mismatched = false;
        var wrongType = new SqlQuery<int, long>("capacity.mismatch", $"SELECT id FROM \"{_schema}\".orders WHERE tenant = $1::int4 AND id = 1",
            [new("id", "int4", false)], static (command, tenant) => command.Parameters.Add(new BlueTuskParameter<int>(tenant) { PostgreSqlTypeOid = 23u }),
            static reader => reader.GetFieldValue<int>(0), maximumRows: 1);
        try { _ = await DrainAsync(wrongType, 0, cancellationToken); }
        catch (SqlContractMismatchException) { mismatched = true; }
        recorder.Invariant("contract-mismatch-fails-closed", mismatched, "A result shape that differs from its contract must be refused.");

        var before = await CountAsync(cancellationToken);
        var refused = false;
        var write = new SqlQuery<int, long>("capacity.write", $"DELETE FROM \"{_schema}\".orders WHERE tenant = $1::int4 RETURNING id",
            [new("id", "int8", false)], static (command, tenant) => command.Parameters.Add(new BlueTuskParameter<int>(tenant) { PostgreSqlTypeOid = 23u }),
            static reader => reader.GetFieldValue<long>(0), maximumRows: 10);
        try { _ = await DrainAsync(write, 0, cancellationToken); }
        catch (ArgumentException) { refused = true; }
        var afterRows = await CountAsync(cancellationToken);
        recorder.Invariant("write-statement-refused", refused && before == afterRows, $"rowsBefore={before}, rowsAfter={afterRows}");

        var expected = (long)_tenants * _orders;
        await using var command = _source.CreateCommand($"""
            SELECT count(*) FILTER (WHERE checksum <> id * 31 + version * 7 + tenant) FROM "{_schema}".orders
            """);
        var torn = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        recorder.Invariant("application-rows-exact", afterRows == expected && torn == 0, $"rows={afterRows}, expected={expected}, torn={torn}");
    }

    private async Task<int> DrainAsync<TArguments, TResult>(SqlQuery<TArguments, TResult> query, TArguments arguments, CancellationToken cancellationToken)
    {
        var rows = 0;
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await foreach (var _ in query.ReadAsync(connection, arguments, cancellationToken: cancellationToken)) { rows++; }
        return rows;
    }

    private async Task<long> CountAsync(CancellationToken cancellationToken)
    {
        await using var command = _source.CreateCommand($"SELECT count(*) FROM \"{_schema}\".orders");
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = _source.CreateCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE");
            _ = await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await _source.DisposeAsync();
        }
    }
}

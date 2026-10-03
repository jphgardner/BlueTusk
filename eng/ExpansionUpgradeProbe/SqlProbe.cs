using System.Globalization;
using BlueTusk.Data;
using BlueTusk.Sql;
using static BlueTusk.UpgradeProbe.ProbeContext;

namespace BlueTusk.UpgradeProbe;

/// <summary>
/// Typed SQL rehearsal. BlueTusk SQL keeps no durable state of its own, so the persisted state is the
/// application's schema and rows. The baseline validates a typed read contract and reads the rows; the
/// candidate must validate the same contract with the same fingerprint, return identical rows without
/// changing the schema, stop server-side production at the documented overflow sentinel and refuse a
/// non-read statement without effect. The baseline then reads the rows the candidate left.
/// </summary>
internal static class FamilyProbe
{
    public const string Family = "Sql";

    public static async Task RunAsync(ProbeContext context)
    {
        var app = context.SchemaBase + "_app";
        var orders = new SqlQuery<decimal, OrderRow>("orders-by-amount",
            $"SELECT id, name, amount FROM \"{app}\".orders WHERE amount >= $1::numeric ORDER BY id",
            [new("id", "int4", false), new("name", "text", false), new("amount", "numeric", false)],
            static (command, minimum) => command.Parameters.Add(new BlueTuskParameter<decimal>(minimum)),
            static reader => new OrderRow(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2)),
            maximumRows: 100);
        await context.FingerprintAsync("before", app);
        if (context.IsSeed)
        {
            // The application's own migration: typed SQL never owns DDL.
            await context.ExecuteAsync($"""
                CREATE SCHEMA "{app}";
                CREATE TABLE "{app}".orders (id integer PRIMARY KEY, name text NOT NULL, amount numeric(10, 2) NOT NULL);
                CREATE SEQUENCE "{app}".overflow_seq;
                INSERT INTO "{app}".orders VALUES (1, 'one', 10.00), (2, 'two', 20.50), (3, 'three', 30.25);
                """);
        }

        await orders.ValidateAsync(context.DataSource, 0m);
        await context.FingerprintAsync("after", app);
        context.Observe("ContractsValidated", 1);
        var rows = await ReadAsync(context, orders);
        context.Observe("RowsRead", rows.Length);

        if (context.IsSeed)
        {
            context.WriteState(new() { ["rows"] = string.Join('|', rows), ["fingerprint"] = orders.Fingerprint });
            return;
        }

        var handoff = context.ReadState();
        Require(orders.Fingerprint == handoff["fingerprint"], "The typed query contract fingerprint changed across the binary boundary.");
        context.Observe("QueryFingerprintStable", 1);
        if (context.IsRollback)
        {
            Require(string.Join('|', rows) == handoff["rows"], "The rolled-back binary read different application rows.");
            return;
        }

        Require(string.Join('|', rows) == handoff["rows"], "The candidate read different application rows than the baseline.");
        context.Observe("ResultsIdentical", 1);
        var overflow = new SqlQuery<int, long>("bounded-rows",
            $"SELECT nextval('\"{app}\".overflow_seq') AS value FROM generate_series(1, 100000)",
            [new("value", "int8", false)], static (_, _) => { }, static reader => reader.GetInt64(0), maximumRows: 2);
        var delivered = 0;
        await RequireRejectedAsync<SqlResultLimitException>(async () =>
        {
            await using var connection = await context.DataSource.OpenConnectionAsync();
            await foreach (var _ in overflow.ReadAsync(connection, 0))
            {
                delivered++;
            }
        }, "An over-limit typed read completed without the documented result limit.");
        context.Observe("OverflowRowsDelivered", delivered);
        // sql/README.md: PostgreSQL sends at most the declared rows plus one overflow sentinel.
        context.Observe("ServerRowsProduced", await context.CountAsync($"SELECT last_value FROM \"{app}\".overflow_seq"));
        var write = new SqlQuery<int, int>("non-read",
            $"INSERT INTO \"{app}\".orders (id, name, amount) VALUES (99, 'write', 1) RETURNING id",
            [new("id", "int4", false)], static (_, _) => { }, static reader => reader.GetInt32(0), maximumRows: 1);
        await RequireRejectedAsync<ArgumentException>(async () =>
        {
            await using var connection = await context.DataSource.OpenConnectionAsync();
            await foreach (var _ in write.ReadAsync(connection, 0))
            {
            }
        }, "The candidate executed a non-read statement through a typed read.");
        Require(await context.CountAsync($"SELECT count(*) FROM \"{app}\".orders WHERE id = 99") == 0, "A refused non-read statement left an effect.");
        context.Observe("NonReadRejected", 1);
        await context.ExecuteAsync($"INSERT INTO \"{app}\".orders VALUES (4, 'four', 40.75)");
        handoff["rows"] += "|4:four:40.75";
        context.WriteState(handoff);
    }

    private static async Task<string[]> ReadAsync(ProbeContext context, SqlQuery<decimal, OrderRow> query)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var rows = new List<string>();
        await foreach (var row in query.ReadAsync(connection, 0m))
        {
            rows.Add(string.Create(CultureInfo.InvariantCulture, $"{row.Id}:{row.Name}:{row.Amount:0.00}"));
        }

        return [.. rows];
    }

    private sealed record OrderRow(int Id, string Name, decimal Amount);
}

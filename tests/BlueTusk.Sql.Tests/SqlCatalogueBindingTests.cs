using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Sql.Tests;

public sealed class SqlCatalogueBindingTests
{
    [Fact]
    public async Task Qualified_enum_and_domain_bindings_round_trip_and_reject_same_named_other_schema()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } value
            ? value : throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is required for live PostgreSQL evidence.");
        var suffix = Guid.NewGuid().ToString("N");
        var first = "sql_types_a_" + suffix;
        var second = "sql_types_b_" + suffix;
        await using var admin = BlueTuskDataSource.Create(connectionString);
        try
        {
            await using (var setup = admin.CreateCommand($"""
                CREATE SCHEMA {first}; CREATE SCHEMA {second};
                CREATE TYPE {first}.state AS ENUM ('new', 'done');
                CREATE TYPE {second}.state AS ENUM ('new', 'done');
                CREATE DOMAIN {first}.positive AS int4 CHECK (VALUE > 0);
                CREATE DOMAIN {second}.positive AS int4 CHECK (VALUE > 0);
                """))
            { _ = await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); }

            await using var source = BlueTuskDataSource.Create(connectionString);
            var arguments = new Arguments(new BlueTuskEnumValue("new"), 42,
                [new BlueTuskEnumValue("new"), new BlueTuskEnumValue("done")], [1, 2, 3], null);
            var query = CreateQuery(first, first);
            await query.ValidateAsync(source, arguments, TestContext.Current.CancellationToken);
            await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
            var count = 0;
            await foreach (var row in query.ReadAsync(connection, arguments,
                cancellationToken: TestContext.Current.CancellationToken))
            {
                Assert.Equal(arguments.State, row.State);
                Assert.Equal(arguments.Amount, row.Amount);
                Assert.Equal(arguments.States, row.States);
                Assert.Equal(arguments.Amounts, row.Amounts);
                Assert.Null(row.MaybeState);
                count++;
            }
            Assert.Equal(1, count);

            await Assert.ThrowsAsync<SqlContractMismatchException>(() =>
                CreateQuery(first, second).ValidateAsync(source, arguments, TestContext.Current.CancellationToken).AsTask());
            await Assert.ThrowsAsync<SqlContractMismatchException>(() =>
                CreateQuery(first, first, wrongDomainBase: true).ValidateAsync(source, arguments,
                    TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA IF EXISTS {first} CASCADE; DROP SCHEMA IF EXISTS {second} CASCADE");
            _ = await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static SqlQuery<Arguments, Row> CreateQuery(string sqlSchema, string declaredSchema, bool wrongDomainBase = false)
    {
        var domainBase = wrongDomainBase ? "int8" : "int4";
        return new SqlQuery<Arguments, Row>("CatalogueTypes", $"""
            SELECT $1::{sqlSchema}.state AS "State", $2::{sqlSchema}.positive AS "Amount",
                $3::{sqlSchema}.state[] AS "States", $4::{sqlSchema}.positive[] AS "Amounts",
                $5::{sqlSchema}.state AS "MaybeState"
            """,
            [
                new("State", $"enum:{declaredSchema}.state", false),
                new("Amount", $"domain:{declaredSchema}.positive:{domainBase}", false),
                new("States", $"enum:{declaredSchema}.state[]", false),
                new("Amounts", $"domain:{declaredSchema}.positive:{domainBase}[]", false),
                new("MaybeState", $"enum:{declaredSchema}.state", true),
            ],
            (command, values) =>
            {
                command.Parameters.Add(new BlueTuskParameter<BlueTuskEnumValue>(values.State)
                { PostgreSqlTypeName = $"{sqlSchema}.state" });
                command.Parameters.Add(new BlueTuskParameter<int>(values.Amount)
                { PostgreSqlTypeName = $"{sqlSchema}.positive" });
                command.Parameters.Add(new BlueTuskParameter<BlueTuskEnumValue[]>(values.States)
                { PostgreSqlTypeName = $"{sqlSchema}.state[]" });
                command.Parameters.Add(new BlueTuskParameter<int[]>(values.Amounts)
                { PostgreSqlTypeName = $"{sqlSchema}.positive[]" });
                command.Parameters.Add(new BlueTuskParameter<BlueTuskEnumValue?>(values.MaybeState)
                { PostgreSqlTypeName = $"{sqlSchema}.state" });
            },
            static reader => new Row(reader.GetFieldValue<BlueTuskEnumValue>(0), reader.GetFieldValue<int>(1),
                reader.GetFieldValue<BlueTuskEnumValue[]>(2), reader.GetFieldValue<int[]>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<BlueTuskEnumValue>(4)));
    }

    private sealed record Arguments(BlueTuskEnumValue State, int Amount, BlueTuskEnumValue[] States,
        int[] Amounts, BlueTuskEnumValue? MaybeState);

    private sealed record Row(BlueTuskEnumValue State, int Amount, BlueTuskEnumValue[] States,
        int[] Amounts, BlueTuskEnumValue? MaybeState);
}

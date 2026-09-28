using System.Data;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Schema;
using BlueTusk.Sql.SourceGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit.Sdk;

namespace BlueTusk.Sql.Tool.Tests;

[Collection(SqlCatalogueValidationDefinition.Name)]
public sealed class SqlCliTests
{
    [Fact]
    public async Task Live_validation_produces_offline_build_receipt_and_rejects_source_or_snapshot_changes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await fixture.CaptureAsync(token);
        var sql = fixture.QueryText("int8");
        await File.WriteAllTextAsync(fixture.Query, sql, token);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await SqlCli.RunAsync(fixture.Arguments, output, error, fixture.DataSource, token));
        var receipt = await File.ReadAllTextAsync(fixture.Receipt, token);
        var snapshot = await File.ReadAllTextAsync(fixture.Snapshot, token);
        var generated = Run(new TextFile("query.sql", sql), new TextFile("app.bluetusk-sql.xml", receipt), new TextFile("app.bluetusk-schema.json", snapshot));
        Assert.Empty(generated.Diagnostics);
        Assert.Contains("ValidatedSchemaFingerprint", Assert.Single(Assert.Single(generated.Results).GeneratedSources).SourceText.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());

        var staleSql = Run(new TextFile("query.sql", sql.Replace("LIMIT 5", "LIMIT 6", StringComparison.Ordinal)),
            new TextFile("app.bluetusk-sql.xml", receipt), new TextFile("app.bluetusk-schema.json", snapshot));
        Assert.Contains(staleSql.Diagnostics, diagnostic => diagnostic.Id == "BTS003");
        var staleSnapshot = Run(new TextFile("query.sql", sql), new TextFile("app.bluetusk-sql.xml", receipt),
            new TextFile("app.bluetusk-schema.json", snapshot + " "));
        Assert.Contains(staleSnapshot.Diagnostics, diagnostic => diagnostic.Id == "BTS003");
        Assert.Contains(Run(new TextFile("query.sql", sql)).Diagnostics, diagnostic => diagnostic.Id == "BTS003");
        Assert.DoesNotContain(Directory.EnumerateFiles(fixture.Directory), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Catalogue_receipts_cover_enum_domain_and_routine_drift_without_relation_changes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await fixture.CreateTypeContractsAsync(token);
        var baseline = await fixture.CaptureCatalogueAsync(token);
        await File.WriteAllTextAsync(fixture.Query, fixture.QueryText("int8"), token);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await SqlCli.RunAsync(fixture.CatalogueArguments, output, error, fixture.DataSource, token));
        var receipt = await File.ReadAllTextAsync(fixture.Receipt, token);
        var snapshot = await File.ReadAllTextAsync(fixture.CatalogueSnapshot, token);
        var generated = Run(new TextFile("query.sql", fixture.QueryText("int8")),
            new TextFile("app.bluetusk-sql.xml", receipt), new TextFile("app.bluetusk-catalog.json", snapshot));
        Assert.Empty(generated.Diagnostics);
        Assert.Contains("ValidatedSchemaFingerprint = \"" + baseline.Fingerprint + "\"",
            Assert.Single(Assert.Single(generated.Results).GeneratedSources).SourceText.ToString(), StringComparison.Ordinal);
        Assert.Contains(Run(new TextFile("query.sql", fixture.QueryText("int8")),
            new TextFile("app.bluetusk-sql.xml", receipt), new TextFile("app.bluetusk-catalog.json", snapshot + " ")).Diagnostics,
            diagnostic => diagnostic.Id == "BTS003");
        Assert.Contains(Run(new TextFile("query.sql", fixture.QueryText("int8")), new TextFile("app.bluetusk-sql.xml", receipt),
            new TextFile("app.bluetusk-catalog.json", snapshot), new TextFile("app.bluetusk-schema.json", "{}")).Diagnostics,
            diagnostic => diagnostic.Id == "BTS003");

        foreach (var ddl in new[]
        {
            $"CREATE OR REPLACE FUNCTION \"{fixture.Schema}\".value() RETURNS text LANGUAGE SQL AS 'SELECT ''private-routine-marker''::text'",
            $"ALTER TYPE \"{fixture.Schema}\".state ADD VALUE 'pending'",
            $"ALTER DOMAIN \"{fixture.Schema}\".positive ADD CONSTRAINT upper_bound CHECK (VALUE < 1000)",
        })
        {
            await using (var change = fixture.DataSource.CreateCommand(ddl)) { _ = await change.ExecuteNonQueryAsync(token); }
            Assert.Equal(baseline.Relations.Fingerprint,
                (await new PostgreSqlSchemaCapture(fixture.DataSource, new() { Schemas = [fixture.Schema] }).CaptureAsync(token)).Fingerprint);
            Assert.Equal(2, await SqlCli.RunAsync(fixture.CatalogueArguments, output, error, fixture.DataSource, token));
            Assert.Equal(receipt, await File.ReadAllTextAsync(fixture.Receipt, token));
            Assert.DoesNotContain("private-routine-marker", error.ToString(), StringComparison.Ordinal);
            baseline = await fixture.CaptureCatalogueAsync(token);
        }
    }

    [Fact]
    public async Task Catalogue_receipt_validates_qualified_enum_and_domain_scalar_nullable_and_array_bindings()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await fixture.CreateTypeContractsAsync(token);
        await fixture.DataSource.ReloadTypesAsync(token);
        _ = await fixture.CaptureCatalogueAsync(token);
        var sql = $"""
            -- bluetusk-query: Example.CatalogueTypes
            -- bluetusk-validation: required
            -- bluetusk-param: state enum:{fixture.Schema}.state required
            -- bluetusk-param: amount domain:{fixture.Schema}.positive:int4 required
            -- bluetusk-param: states enum:{fixture.Schema}.state[] nullable
            -- bluetusk-param: amounts domain:{fixture.Schema}.positive:int4[] nullable
            -- bluetusk-result: State enum:{fixture.Schema}.state required
            -- bluetusk-result: Amount domain:{fixture.Schema}.positive:int4 required
            -- bluetusk-result: States enum:{fixture.Schema}.state[] nullable
            -- bluetusk-result: Amounts domain:{fixture.Schema}.positive:int4[] nullable
            SELECT $1::"{fixture.Schema}".state AS "State", $2::"{fixture.Schema}".positive AS "Amount",
                $3::"{fixture.Schema}".state[] AS "States", $4::"{fixture.Schema}".positive[] AS "Amounts";
            """;
        await File.WriteAllTextAsync(fixture.Query, sql, token);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await SqlCli.RunAsync(fixture.CatalogueArguments, output, error, fixture.DataSource, token));
        var receipt = await File.ReadAllTextAsync(fixture.Receipt, token);
        var snapshot = await File.ReadAllTextAsync(fixture.CatalogueSnapshot, token);
        var generated = Run(new TextFile("query.sql", sql), new TextFile("app.bluetusk-sql.xml", receipt),
            new TextFile("app.bluetusk-catalog.json", snapshot));
        Assert.Empty(generated.Diagnostics);
        var source = Assert.Single(Assert.Single(generated.Results).GeneratedSources).SourceText.ToString();
        Assert.Contains($"PostgreSqlTypeName = \"{fixture.Schema}.state[]\"", source, StringComparison.Ordinal);
        Assert.Contains($"PostgreSqlTypeName = \"{fixture.Schema}.positive\"", source, StringComparison.Ordinal);
        Assert.Contains("BlueTuskEnumValue[]? States", source, StringComparison.Ordinal);
        Assert.Contains("int[]? Amounts", source, StringComparison.Ordinal);
        Assert.Contains("GetFieldValue<global::BlueTusk.TypeSystem.BlueTuskEnumValue[]>", source, StringComparison.Ordinal);

        var wrongBase = sql.Replace(".positive:int4", ".positive:int8", StringComparison.Ordinal);
        Assert.NotEqual(sql, wrongBase);
        var wrongQuery = SqlContractModel.Parse(new SqlContractModel.SqlSource(fixture.Query, wrongBase), out _)!;
        Assert.Equal(20u, wrongQuery.Parameters[1].Type.BaseOid);
        Assert.Equal(20u, wrongQuery.Columns[1].Type.BaseOid);
        await File.WriteAllTextAsync(fixture.Query, wrongBase, token);
        Assert.Equal(1, await SqlCli.RunAsync(fixture.CatalogueArguments, output, error, fixture.DataSource, token));
        Assert.Equal(receipt, await File.ReadAllTextAsync(fixture.Receipt, token));
        await File.WriteAllTextAsync(fixture.Query, sql, token);
        await fixture.CaptureAsync(token);
        Assert.Equal(1, await SqlCli.RunAsync(fixture.Arguments, output, error, fixture.DataSource, token));
        Assert.Equal(receipt, await File.ReadAllTextAsync(fixture.Receipt, token));
    }

    [Fact]
    public async Task Concurrent_routine_change_during_query_description_rejects_catalogue_receipt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await fixture.CreateTypeContractsAsync(token);
        _ = await fixture.CaptureCatalogueAsync(token);
        await File.WriteAllTextAsync(fixture.Query, fixture.QueryText("int8"), token);
        await File.WriteAllTextAsync(fixture.Receipt, "prior-receipt", token);
        using var output = new StringWriter();
        using var error = new StringWriter();
        await using var blocker = await fixture.DataSource.OpenConnectionAsync(token);
        await using var transaction = await blocker.BeginTransactionAsync(token);
        await using (var hold = blocker.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText = $"LOCK TABLE \"{fixture.Schema}\".items IN ACCESS EXCLUSIVE MODE";
            _ = await hold.ExecuteNonQueryAsync(token);
        }
        var validation = SqlCli.RunAsync(fixture.CatalogueArguments, output, error, fixture.DataSource, token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                if (validation.IsCompleted) { throw new InvalidOperationException("Validation completed before the controlled descriptor barrier."); }
                await using var observe = fixture.DataSource.CreateCommand("""
                    SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity
                    WHERE datname = pg_catalog.current_database() AND wait_event_type = 'Lock'
                        AND query LIKE @wrapper AND query LIKE @schema)
                    """);
                observe.CommandTimeout = 2;
                observe.Parameters.Add(new BlueTuskParameter<string>("%bluetusk_contract%") { ParameterName = "wrapper" });
                observe.Parameters.Add(new BlueTuskParameter<string>("%" + fixture.Schema + "%") { ParameterName = "schema" });
                if ((bool)(await observe.ExecuteScalarAsync(deadline.Token))!) { break; }
                await Task.Delay(20, deadline.Token);
            }
            await using var change = fixture.DataSource.CreateCommand(
                $"CREATE OR REPLACE FUNCTION \"{fixture.Schema}\".value() RETURNS text LANGUAGE SQL AS 'SELECT ''changed-private-body''::text'");
            _ = await change.ExecuteNonQueryAsync(token);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _ = await validation.WaitAsync(TimeSpan.FromSeconds(45), CancellationToken.None);
        }
        Assert.Equal(1, await validation.WaitAsync(TimeSpan.FromSeconds(45), token));
        Assert.Equal("prior-receipt", await File.ReadAllTextAsync(fixture.Receipt, token));
        Assert.DoesNotContain("changed-private-body", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.EnumerateFiles(fixture.Directory), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Optional_receipts_only_emit_validation_metadata_for_the_exact_snapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await fixture.CaptureAsync(token);
        var sql = fixture.QueryText("int8").Replace("-- bluetusk-validation: required", "", StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixture.Query, sql, token);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await SqlCli.RunAsync(fixture.Arguments, output, error, fixture.DataSource, token));
        var receipt = await File.ReadAllTextAsync(fixture.Receipt, token);
        var snapshot = await File.ReadAllTextAsync(fixture.Snapshot, token);
        foreach (var snapshots in new[] { Array.Empty<AdditionalText>(), new AdditionalText[] { new TextFile("app.bluetusk-schema.json", snapshot + " ") } })
        {
            var result = Run([new TextFile("query.sql", sql), new TextFile("app.bluetusk-sql.xml", receipt), .. snapshots]);
            Assert.Empty(result.Diagnostics);
            Assert.Contains("ValidatedSchemaFingerprint = null",
                Assert.Single(Assert.Single(result.Results).GeneratedSources).SourceText.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Live_result_mismatch_drift_and_mutation_rejection_preserve_prior_receipt_and_business_state()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await fixture.CaptureAsync(token);
        await File.WriteAllTextAsync(fixture.Receipt, "preserved", token);
        await File.WriteAllTextAsync(fixture.Query, fixture.QueryText("text"), token);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await SqlCli.RunAsync(fixture.Arguments, output, error, fixture.DataSource, token));
        Assert.Equal("preserved", await File.ReadAllTextAsync(fixture.Receipt, token));

        await File.WriteAllTextAsync(fixture.Query, fixture.QueryText("int8").Replace($"SELECT id AS \"Id\" FROM \"{fixture.Schema}\".items WHERE $1::text IS NOT NULL LIMIT 5;",
            $"WITH gone AS (DELETE FROM \"{fixture.Schema}\".items RETURNING id) SELECT id AS \"Id\" FROM gone WHERE $1::text IS NOT NULL;", StringComparison.Ordinal), token);
        Assert.Equal(1, await SqlCli.RunAsync(fixture.Arguments, output, error, fixture.DataSource, token));
        await using (var count = fixture.DataSource.CreateCommand($"SELECT count(*) FROM \"{fixture.Schema}\".items"))
        { Assert.Equal(1L, await count.ExecuteScalarAsync(token)); }

        await using (var change = fixture.DataSource.CreateCommand($"ALTER TABLE \"{fixture.Schema}\".items ADD COLUMN label text DEFAULT 'private-marker'"))
        { _ = await change.ExecuteNonQueryAsync(token); }
        await File.WriteAllTextAsync(fixture.Query, fixture.QueryText("int8"), token);
        Assert.Equal(2, await SqlCli.RunAsync(fixture.Arguments, output, error, fixture.DataSource, token));
        Assert.Equal("preserved", await File.ReadAllTextAsync(fixture.Receipt, token));
        Assert.DoesNotContain("private-marker", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalogue_capture_reuses_caller_read_only_transaction_without_committing_it()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var capture = new PostgreSqlSchemaCapture(fixture.DataSource, new() { Schemas = [fixture.Schema] });
        await using var connection = await fixture.DataSource.OpenConnectionAsync(token);
        await using (var writable = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => capture.CaptureInTransactionAsync(connection, writable, token).AsTask());
            await writable.RollbackAsync(token);
        }
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using (var setup = connection.CreateCommand())
        {
            setup.Transaction = transaction;
            setup.CommandText = "SET TRANSACTION READ ONLY";
            _ = await setup.ExecuteNonQueryAsync(token);
        }
        Assert.Single((await capture.CaptureInTransactionAsync(connection, transaction, token)).Relations);
        Assert.Same(connection, transaction.Connection);
        await transaction.CommitAsync(token);
    }

    [Fact]
    public async Task Invalid_arguments_and_XML_entities_fail_without_database_access()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await SqlCli.RunAsync(["validate", "--unknown", "secret"], output, error));
        Assert.Equal(1, await SqlCli.RunAsync(["validate", "--query"], output, error));
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        var result = Run(new TextFile("bad.bluetusk-sql.xml", "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///private'>]><BlueTuskSqlValidation>&e;</BlueTuskSqlValidation>"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "BTS003");
    }

    private static GeneratorDriverRunResult Run(params AdditionalText[] files)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new SqlQueryGenerator().AsSourceGenerator()], files,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        return driver.RunGenerators(CSharpCompilation.Create("ReceiptTests")).GetRunResult();
    }

    private sealed class TextFile(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text, Encoding.UTF8);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(BlueTuskDataSource dataSource)
        {
            DataSource = dataSource;
            Directory = Path.Combine(Path.GetTempPath(), "bluetusk-sql-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
        }
        internal BlueTuskDataSource DataSource { get; }
        internal string Directory { get; }
        internal string Schema { get; } = "sql_cli_" + Guid.NewGuid().ToString("N");
        internal string Query => Path.Combine(Directory, "query.sql");
        internal string Snapshot => Path.Combine(Directory, "app.bluetusk-schema.json");
        internal string CatalogueSnapshot => Path.Combine(Directory, "app.bluetusk-catalog.json");
        internal string Receipt => Path.Combine(Directory, "app.bluetusk-sql.xml");
        internal string[] Arguments => ["validate", "--schema", Snapshot, "--schemas", Schema, "--output", Receipt, "--query", Query];
        internal string[] CatalogueArguments => ["validate", "--schema", CatalogueSnapshot, "--schemas", Schema, "--output", Receipt, "--query", Query];
        internal string QueryText(string type) => $"""
            -- bluetusk-query: Example.Items
            -- bluetusk-validation: required
            -- bluetusk-param: tenant text required
            -- bluetusk-result: Id {type} required
            -- bluetusk-max-rows: 5
            SELECT id AS "Id" FROM "{Schema}".items WHERE $1::text IS NOT NULL LIMIT 5;
            """;
        internal async Task CaptureAsync(CancellationToken token) => await File.WriteAllBytesAsync(Snapshot,
            SchemaSnapshotSerializer.Serialize(await new PostgreSqlSchemaCapture(DataSource, new() { Schemas = [Schema] }).CaptureAsync(token)), token);
        internal async Task CreateTypeContractsAsync(CancellationToken token)
        {
            await using var setup = DataSource.CreateCommand($"""
                CREATE TYPE "{Schema}".state AS ENUM ('new','done');
                CREATE DOMAIN "{Schema}".positive AS integer CHECK (VALUE >= 0);
                CREATE FUNCTION "{Schema}".value() RETURNS text LANGUAGE SQL AS 'SELECT ''original''::text';
                """);
            _ = await setup.ExecuteNonQueryAsync(token);
        }
        internal async Task<SchemaCatalogSnapshot> CaptureCatalogueAsync(CancellationToken token)
        {
            var snapshot = await new PostgreSqlSchemaCatalogCapture(DataSource, new() { Relations = new() { Schemas = [Schema] } }).CaptureAsync(token);
            await File.WriteAllBytesAsync(CatalogueSnapshot, SchemaCatalogSerializer.Serialize(snapshot), token);
            return snapshot;
        }
        internal static async Task<Fixture> CreateAsync()
        {
            var settings = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } value
                ? value : throw SkipException.ForSkip("A disposable PostgreSQL fixture is required.");
            var fixture = new Fixture(BlueTuskDataSource.Create(settings));
            await using var setup = fixture.DataSource.CreateCommand($"CREATE SCHEMA \"{fixture.Schema}\"; CREATE TABLE \"{fixture.Schema}\".items(id bigint NOT NULL); INSERT INTO \"{fixture.Schema}\".items VALUES(1)");
            _ = await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await using var drop = DataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
            _ = await drop.ExecuteNonQueryAsync();
            await DataSource.DisposeAsync();
            // This is a uniquely created test directory, never a user-supplied path.
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlCatalogueValidationDefinition
{
    public const string Name = "SQL catalogue validation";
}

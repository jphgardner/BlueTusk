using System.Data;
using System.Text;
using System.Text.Json;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Schema.Tests;

[Collection("Schema catalogue mutation")]
public sealed class SchemaCatalogTests
{
    [Fact]
    public void Catalogues_are_immutable_canonical_and_reject_tampered_or_future_envelopes()
    {
        var labels = new List<string> { "a", "", "🐘" };
        var first = new SchemaTypeContract(new("app", "state"), "e", labels);
        labels.Add("changed-after-construction");
        var domain = new SchemaTypeContract(new("app", "amount"), "d", baseType: "numeric(12,2)", isNullable: false,
            constraints: [new("positive", "c", "CHECK (VALUE >= 0)")]);
        var snapshot = new SchemaCatalogSnapshot(new([]), [first, domain],
            privileges: [new("type", new("app", "state"), null, null, "owner", "PUBLIC", "USAGE", false, true)]);
        var reversed = new SchemaCatalogSnapshot(new([]), [domain, first], privileges: snapshot.Privileges);
        Assert.Equal(snapshot.Fingerprint, reversed.Fingerprint);
        Assert.Equal(3, first.EnumLabels.Count);
        var encoded = SchemaCatalogSerializer.Serialize(snapshot);
        var reopened = SchemaCatalogSerializer.Deserialize(encoded);
        Assert.Equal(snapshot.Fingerprint, reopened.Fingerprint);
        Assert.Empty(SchemaCatalogCompatibility.Compare(snapshot, reopened).Changes);
        Assert.Throws<JsonException>(() => SchemaCatalogSerializer.Deserialize(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(encoded).Replace("numeric(12,2)", "numeric(13,2)", StringComparison.Ordinal))));
        Assert.Throws<JsonException>(() => SchemaCatalogSerializer.Deserialize(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(encoded).Replace("\"FormatVersion\":1", "\"FormatVersion\":2", StringComparison.Ordinal))));
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaCatalogSerializer.Serialize(snapshot, encoded.Length - 1));
    }

    [Fact]
    public void Bounds_reject_before_enumeration_sorting_or_deserialized_graphs_and_duplicate_identities()
    {
        static IEnumerable<SchemaTypeContract> Unbounded()
        { while (true) { yield return new(new("app", "enum"), "e", ["one"]); } }
        var limits = new SchemaCatalogLimits { MaximumEntries = 2 };
        Assert.Throws<SchemaCaptureLimitException>(() => new SchemaCatalogSnapshot(new([]), Unbounded(), limits: limits));
        Assert.Throws<ArgumentException>(() => new SchemaCatalogSnapshot(new([]), [new(new("app", "same"), "e"), new(new("app", "same"), "e")]));
        Assert.Throws<ArgumentException>(() => new SchemaTypeContract(new("app", "bad"), "d", ["label"], "int4"));
        Assert.Throws<ArgumentException>(() => new SchemaTypeContract(new("app", "bad"), "e", ["duplicate", "duplicate"]));
        Assert.Throws<ArgumentException>(() => new SchemaTypeContract(new("app", "bad"), "e", ["\ud800"]));
        var snapshot = new SchemaCatalogSnapshot(new([]), [new(new("app", "enum"), "e", ["one", "two", "three"])]);
        var json = SchemaCatalogSerializer.Serialize(snapshot);
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaCatalogSerializer.Deserialize(json, new() { MaximumEnumLabels = 2 }));
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaCatalogSerializer.Deserialize(json, new() { MaximumMetadataBytes = 32 }));
        Assert.Throws<JsonException>(() => SchemaCatalogSerializer.Deserialize("{\"FormatVersion\":1,\"Types\":[null]}"u8));
    }

    [Fact]
    public void Compatibility_preserves_enum_order_and_reviews_security_and_publication_changes()
    {
        var before = new SchemaCatalogSnapshot(new([]), [new(new("app", "state"), "e", ["new", "done"])],
            publications: [new("changes", new("app", "items"), false, false, true, true, true, true, "id", null)]);
        var after = new SchemaCatalogSnapshot(new([]), [new(new("app", "state"), "e", ["new", "pending", "done"])],
            privileges: [new("type", new("app", "state"), null, null, "owner", "reader", "USAGE", false)],
            publications: [new("changes", new("app", "items"), false, false, true, true, true, true, "id", "tenant = 'a'")]);
        var comparison = SchemaCatalogCompatibility.Compare(before, after);
        Assert.Equal(3, comparison.Changes.Count); Assert.True(comparison.RequiresReview); Assert.False(comparison.HasIncompatibleChanges);
        var reordered = new SchemaCatalogSnapshot(new([]), [new(new("app", "state"), "e", ["done", "new"])]);
        Assert.Contains(SchemaCatalogCompatibility.Compare(before, reordered).Changes, value => value.ContractKind == "type" && value.Impact == SchemaChangeImpact.Incompatible);
        var removed = new SchemaCatalogSnapshot(new([]), [new(new("app", "state"), "e", ["new"])]);
        Assert.True(SchemaCatalogCompatibility.Compare(before, removed).HasIncompatibleChanges);
    }

    [Fact]
    public async Task Live_enum_domain_routine_acl_and_publication_drift_share_one_repeatable_read_snapshot()
    {
        await using var dataSource = BlueTuskDataSource.Create(Connection());
        var schema = "catalog_" + Guid.NewGuid().ToString("N"); var publication = "pub_" + Guid.NewGuid().ToString("N");
        try
        {
            await Execute(dataSource, $"""
                CREATE SCHEMA "{schema}";
                CREATE TYPE "{schema}".state AS ENUM ('new','done');
                CREATE TYPE "{schema}".empty_state AS ENUM ();
                CREATE DOMAIN "{schema}".amount AS numeric(12,2) NOT NULL DEFAULT 0 CONSTRAINT positive CHECK (VALUE >= 0);
                CREATE TABLE "{schema}".items(id bigint PRIMARY KEY, tenant text NOT NULL, state "{schema}".state, amount "{schema}".amount);
                CREATE FUNCTION "{schema}".calculate(value integer) RETURNS integer LANGUAGE SQL IMMUTABLE STRICT AS 'SELECT value + 1';
                CREATE FUNCTION "{schema}".calculate(value bigint) RETURNS bigint LANGUAGE SQL IMMUTABLE AS 'SELECT value + 1';
                CREATE PUBLICATION "{publication}" FOR TABLE "{schema}".items (id,tenant) WHERE (tenant = 'a');
                GRANT SELECT (id) ON "{schema}".items TO PUBLIC;
                """);
            var capture = new PostgreSqlSchemaCatalogCapture(dataSource, new() { Relations = new() { Schemas = [schema] } });
            var before = await capture.CaptureAsync(TestContext.Current.CancellationToken);
            Assert.Equal(3, before.Types.Count); Assert.Equal(2, before.Routines.Count);
            Assert.Empty(Assert.Single(before.Types, value => value.Identity.Name == "empty_state").EnumLabels);
            Assert.False(Assert.Single(before.Types, value => value.Kind == "d").IsNullable);
            Assert.Single(Assert.Single(before.Types, value => value.Kind == "d").Constraints, value => value.Kind == "c");
            Assert.Equal("integer", Assert.Single(before.Routines, value => value.ResultType == "integer").IdentityArguments);
            Assert.Contains(before.Privileges, value => value.ObjectKind == "column" && value.Member == "id" && value.IsPublic);
            var member = Assert.Single(before.Publications); Assert.Equal("id,tenant", member.Columns); Assert.NotNull(member.RowFilterSql);
            Assert.Equal(before.Fingerprint, (await capture.CaptureAsync(TestContext.Current.CancellationToken)).Fingerprint);
            await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, TestContext.Current.CancellationToken);
            await using (var setup = connection.CreateCommand())
            { setup.Transaction = transaction; setup.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL search_path = pg_catalog"; await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); }
            var held = await capture.CaptureInTransactionAsync(connection, transaction, TestContext.Current.CancellationToken);
            await Execute(dataSource, $"""
                ALTER TYPE "{schema}".state ADD VALUE 'pending' BEFORE 'done';
                ALTER DOMAIN "{schema}".amount DROP CONSTRAINT positive;
                ALTER DOMAIN "{schema}".amount ADD CONSTRAINT positive CHECK (VALUE >= 10);
                CREATE OR REPLACE FUNCTION "{schema}".calculate(value integer) RETURNS integer LANGUAGE SQL IMMUTABLE STRICT AS 'SELECT value + 2';
                REVOKE SELECT (id) ON "{schema}".items FROM PUBLIC;
                ALTER PUBLICATION "{publication}" SET TABLE "{schema}".items (id,tenant) WHERE (tenant = 'b');
                """);
            await Assert.ThrowsAsync<SchemaConcurrentDdlException>(() =>
                capture.CaptureInTransactionAsync(connection, transaction, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(before.Fingerprint, held.Fingerprint);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
            var after = await capture.CaptureAsync(TestContext.Current.CancellationToken);
            Assert.Equal(before.Relations.Fingerprint, after.Relations.Fingerprint);
            Assert.NotEqual(before.Fingerprint, after.Fingerprint);
            var diff = SchemaCatalogCompatibility.Compare(before, after);
            Assert.Equal(2, diff.Changes.Count(value => value.ContractKind == "type"));
            Assert.Contains(diff.Changes, value => value.ContractKind == "routine");
            Assert.Contains(diff.Changes, value => value.ContractKind == "privilege" && value.Kind == SchemaCatalogChangeKind.Removed);
            Assert.Contains(diff.Changes, value => value.ContractKind == "publication");
            Assert.Equal(after.Fingerprint, SchemaCatalogSerializer.Deserialize(SchemaCatalogSerializer.Serialize(after)).Fingerprint);
        }
        finally { await Execute(dataSource, $"DROP PUBLICATION IF EXISTS \"{publication}\"; DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"); }
    }

    [Fact]
    public async Task Live_limits_reject_large_bodies_before_wire_allocation_and_leave_pool_healthy()
    {
        await using var dataSource = BlueTuskDataSource.Create(Connection());
        var schema = "catalog_bound_" + Guid.NewGuid().ToString("N");
        try
        {
            await Execute(dataSource, $"CREATE SCHEMA \"{schema}\"; CREATE FUNCTION \"{schema}\".large() RETURNS text LANGUAGE SQL AS 'SELECT ''{new string('x', 8192)}''::text'");
            var limited = new PostgreSqlSchemaCatalogCapture(dataSource, new()
            { Relations = new() { Schemas = [schema] }, Limits = new() { Relations = new() { MaximumStringBytes = 1024 } } });
            await Assert.ThrowsAsync<SchemaCaptureLimitException>(() => limited.CaptureAsync(TestContext.Current.CancellationToken).AsTask());
            var healthy = new PostgreSqlSchemaCatalogCapture(dataSource, new() { Relations = new() { Schemas = [schema] } });
            Assert.Single((await healthy.CaptureAsync(TestContext.Current.CancellationToken)).Routines);
            var rowBound = new PostgreSqlSchemaCatalogCapture(dataSource, new()
            { Relations = new() { Schemas = [schema] }, Limits = new() { MaximumEntries = 1 } });
            await Assert.ThrowsAsync<SchemaCaptureLimitException>(() => rowBound.CaptureAsync(TestContext.Current.CancellationToken).AsTask());
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => healthy.CaptureAsync(cancelled.Token).AsTask());
            Assert.Single((await healthy.CaptureAsync(TestContext.Current.CancellationToken)).Routines);
        }
        finally { await Execute(dataSource, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"); }
    }

    [Fact]
    public void Domain_and_relation_limits_reject_large_JSON_graphs_before_materializing_their_members()
    {
        var checks = Enumerable.Range(0, 20_000).Select(index => new SchemaConstraint("check_" + index, "c", "CHECK (VALUE >= 0)")).ToArray();
        var domains = new SchemaCatalogSnapshot(new([]), [new(new("app", "amount"), "d", baseType: "numeric", constraints: checks)]);
        var domainJson = SchemaCatalogSerializer.Serialize(domains);
        var started = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaCatalogSerializer.Deserialize(domainJson, new() { MaximumDomainConstraints = 1 }));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - started < 64 * 1024, "Domain admission allocated a DTO graph before rejecting its width.");
        var relations = new SchemaCatalogSnapshot(new([new(new("app", "items"), "r", false, false, "d", [new("id", 1, "integer", false)], constraints: checks)]));
        var relationJson = SchemaCatalogSerializer.Serialize(relations);
        started = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaCatalogSerializer.Deserialize(relationJson, new()
        { MaximumDomainConstraints = 100_000, Relations = new() { MaximumConstraints = 1 } }));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - started < 64 * 1024, "Relation admission borrowed the domain graph allowance.");
        started = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaCatalogSerializer.Deserialize(relationJson, new()
        { Relations = new() { MaximumMetadataBytes = 1024 } }));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - started < 64 * 1024, "Relation admission borrowed the outer metadata allowance.");
    }

    [Theory]
    [InlineData('<', 800_000)]
    [InlineData('漢', 700_000)]
    public void Escaped_JSON_roundtrips_admitted_decoded_UTF8_bodies_and_keeps_byte_bounds(char value, int count)
    {
        var body = new string(value, count);
        var relation = new SchemaSnapshot([new(new("app", "view"), "v", false, false, "d", [], definitionSql: body)]);
        var serializedRelation = SchemaSnapshotSerializer.Serialize(relation);
        Assert.Equal(relation.Fingerprint, SchemaSnapshotSerializer.Deserialize(serializedRelation).Fingerprint);
        var catalog = new SchemaCatalogSnapshot(relation,
            routines: [new(new("app", "routine"), "", "f", "text", "sql", false, false, "v", "u", body)]);
        var serializedCatalog = SchemaCatalogSerializer.Serialize(catalog);
        Assert.Equal(catalog.Fingerprint, SchemaCatalogSerializer.Deserialize(serializedCatalog).Fingerprint);
        var decodedBytes = Encoding.UTF8.GetByteCount(body);
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaSnapshotSerializer.Deserialize(serializedRelation,
            new() { MaximumStringBytes = decodedBytes - 1 }));
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaCatalogSerializer.Deserialize(serializedCatalog,
            new() { Relations = new() { MaximumStringBytes = decodedBytes - 1 } }));
    }

    [Fact]
    public async Task Empty_acl_security_definer_and_publication_owner_changes_alter_fingerprints()
    {
        await using var source = BlueTuskDataSource.Create(Connection());
        var identity = Guid.NewGuid().ToString("N"); var schema = "catalog_owner_" + identity;
        var role = "owner_" + identity; var publication = "owner_pub_" + identity;
        try
        {
            await Execute(source, $"""
                CREATE ROLE "{role}" NOLOGIN;
                CREATE SCHEMA "{schema}";
                CREATE TABLE "{schema}".items(id int);
                CREATE FUNCTION "{schema}".privileged() RETURNS integer LANGUAGE SQL SECURITY DEFINER AS 'SELECT 1';
                REVOKE ALL ON FUNCTION "{schema}".privileged() FROM PUBLIC,postgres;
                CREATE PUBLICATION "{publication}" FOR TABLE "{schema}".items;
                """);
            var capture = new PostgreSqlSchemaCatalogCapture(source, new() { Relations = new() { Schemas = [schema] } });
            var before = await capture.CaptureAsync(TestContext.Current.CancellationToken);
            Assert.Contains(before.Privileges, value => value.ObjectKind == "routine" && value.Privilege == "OWNER" && value.Grantee == "postgres");
            await Execute(source, $"ALTER FUNCTION \"{schema}\".privileged() OWNER TO \"{role}\"; ALTER PUBLICATION \"{publication}\" OWNER TO \"{role}\"");
            var after = await capture.CaptureAsync(TestContext.Current.CancellationToken);
            Assert.Equal(Assert.Single(before.Routines).DefinitionSql, Assert.Single(after.Routines).DefinitionSql);
            Assert.Equal(before.Relations.Fingerprint, after.Relations.Fingerprint);
            Assert.NotEqual(before.Fingerprint, after.Fingerprint);
            Assert.Contains(after.Privileges, value => value.ObjectKind == "routine" && value.Privilege == "OWNER" && value.Grantee == role);
            Assert.Contains(after.Privileges, value => value.ObjectKind == "publication" && value.Privilege == "OWNER" && value.Grantee == role);
            var plan = SchemaDeploymentPlan.Create(before, after,
                [new("routine", [new("routine", new(schema, "privileged"), "")]), new("publication", [new("publication", new(schema, "items"), publication)])]);
            Assert.Equal(2, plan.AffectedConsumers.Count);
        }
        finally
        { await Execute(source, $"DROP PUBLICATION IF EXISTS \"{publication}\"; DROP SCHEMA IF EXISTS \"{schema}\" CASCADE; DROP ROLE IF EXISTS \"{role}\""); }
    }

    [Fact]
    public async Task Latin1_catalogue_and_nested_relation_admission_bound_negotiated_UTF8_bytes()
    {
        await using var administrator = BlueTuskDataSource.Create(Connection());
        var database = "bt_catalog_latin_" + Guid.NewGuid().ToString("N");
        try
        {
            await Execute(administrator, $"CREATE DATABASE \"{database}\" TEMPLATE template0 ENCODING 'LATIN1' LC_COLLATE 'C' LC_CTYPE 'C'");
            var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = Connection() };
            builder["Database"] = database;
            await using (var latin = BlueTuskDataSource.Create(builder.ConnectionString))
            {
                var text = new string('é', 800);
                await Execute(latin, $"CREATE SCHEMA app; CREATE FUNCTION app.accented() RETURNS text LANGUAGE SQL AS 'SELECT ''{text}''::text'");
                var limited = new PostgreSqlSchemaCatalogCapture(latin, new()
                { Relations = new() { Schemas = ["app"] }, Limits = new() { Relations = new() { MaximumStringBytes = 1200 } } });
                await Assert.ThrowsAsync<SchemaCaptureLimitException>(() => limited.CaptureAsync(TestContext.Current.CancellationToken).AsTask());
                var healthy = new PostgreSqlSchemaCatalogCapture(latin, new() { Relations = new() { Schemas = ["app"] } });
                Assert.Contains(text, Assert.Single((await healthy.CaptureAsync(TestContext.Current.CancellationToken)).Routines).DefinitionSql, StringComparison.Ordinal);
                await Execute(latin, $"CREATE VIEW app.accents AS SELECT '{text}'::text AS label");
                var relationCapture = new PostgreSqlSchemaCapture(latin, new() { Schemas = ["app"] }, new() { MaximumStringBytes = 1200 });
                await Assert.ThrowsAsync<SchemaCaptureLimitException>(() => relationCapture.CaptureAsync(TestContext.Current.CancellationToken).AsTask());
                var byteCapture = new PostgreSqlSchemaCapture(latin, new() { Schemas = ["app"], MaximumMetadataBytes = 1024 });
                await Assert.ThrowsAsync<SchemaCaptureLimitException>(() => byteCapture.CaptureAsync(TestContext.Current.CancellationToken).AsTask());
                Assert.Single((await new PostgreSqlSchemaCapture(latin, new() { Schemas = ["app"] }).CaptureAsync(TestContext.Current.CancellationToken)).Relations);
            }
        }
        finally { await Execute(administrator, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)"); }
    }

    private static string Connection() => Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } value
        ? value : throw SkipException.ForSkip("A disposable PostgreSQL fixture is required.");
    private static async Task Execute(BlueTuskDataSource source, string sql)
    { await using var command = source.CreateCommand(sql); await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); }

    [Fact]
    public async Task Publication_schema_all_table_and_partition_root_contracts_are_scoped_MVCC_rows()
    {
        await using var source = BlueTuskDataSource.Create(Connection());
        var identity = Guid.NewGuid().ToString("N"); var schema = "catalog_pub_" + identity;
        var explicitPublication = "explicit_" + identity; var schemaPublication = "schema_" + identity; var allPublication = "all_" + identity;
        try
        {
            await Execute(source, $"""
                CREATE SCHEMA "{schema}";
                CREATE TABLE "{schema}".root(id int NOT NULL, tenant text) PARTITION BY RANGE(id);
                CREATE TABLE "{schema}".leaf PARTITION OF "{schema}".root FOR VALUES FROM (0) TO (100);
                CREATE TABLE "{schema}".other(id int);
                CREATE PUBLICATION "{explicitPublication}" FOR TABLE "{schema}".root WITH (publish_via_partition_root=true);
                CREATE PUBLICATION "{schemaPublication}" FOR TABLES IN SCHEMA "{schema}";
                CREATE PUBLICATION "{allPublication}" FOR ALL TABLES WITH (publish='insert',publish_via_partition_root=true);
                """);
            var capture = new PostgreSqlSchemaCatalogCapture(source, new() { Relations = new() { Schemas = [schema] } });
            var catalog = await capture.CaptureAsync(TestContext.Current.CancellationToken);
            var declaredRoot = Assert.Single(catalog.Publications, value => value.Publication == explicitPublication);
            Assert.Equal("root", declaredRoot.Relation.Name); Assert.True(declaredRoot.ViaPartitionRoot);
            Assert.Equal(3, catalog.Publications.Count(value => value.Publication == schemaPublication));
            Assert.Equal(3, catalog.Publications.Count(value => value.Publication == allPublication));
            Assert.All(catalog.Publications, value => Assert.Equal(schema, value.Relation.Schema));
            Assert.All(catalog.Publications.Where(value => value.Publication == allPublication), value =>
            { Assert.True(value.AllTables); Assert.True(value.PublishesInsert); Assert.False(value.PublishesUpdate); Assert.False(value.PublishesDelete); });
        }
        finally
        { await Execute(source, $"DROP PUBLICATION IF EXISTS \"{explicitPublication}\",\"{schemaPublication}\",\"{allPublication}\"; DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"); }
    }

    [Fact]
    public async Task Transient_publication_add_drop_never_returns_intermediate_cached_membership()
    {
        await using var source = BlueTuskDataSource.Create(Connection());
        var schema = "catalog_aba_" + Guid.NewGuid().ToString("N"); var publication = "aba_" + Guid.NewGuid().ToString("N");
        try
        {
            await Execute(source, $"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".items(id int); CREATE PUBLICATION \"{publication}\"");
            var capture = new PostgreSqlSchemaCatalogCapture(source, new() { Relations = new() { Schemas = [schema] } });
            await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, TestContext.Current.CancellationToken);
            await using (var setup = connection.CreateCommand())
            { setup.Transaction = transaction; setup.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL search_path = pg_catalog"; await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); }
            var held = await capture.CaptureInTransactionAsync(connection, transaction, TestContext.Current.CancellationToken);
            Assert.Empty(held.Publications);
            await Execute(source, $"ALTER PUBLICATION \"{publication}\" ADD TABLE \"{schema}\".items");
            await Assert.ThrowsAsync<SchemaConcurrentDdlException>(() => capture.CaptureInTransactionAsync(connection, transaction, TestContext.Current.CancellationToken).AsTask());
            await Execute(source, $"ALTER PUBLICATION \"{publication}\" DROP TABLE \"{schema}\".items");
            try
            {
                var repeated = await capture.CaptureInTransactionAsync(connection, transaction, TestContext.Current.CancellationToken);
                Assert.Empty(repeated.Publications); Assert.Equal(held.Fingerprint, repeated.Fingerprint);
            }
            catch (SchemaConcurrentDdlException) { /* A persistent catalogue tuple update is a conservative rejection. */ }
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
            Assert.Empty((await capture.CaptureAsync(TestContext.Current.CancellationToken)).Publications);
        }
        finally { await Execute(source, $"DROP PUBLICATION IF EXISTS \"{publication}\"; DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"); }
    }
}

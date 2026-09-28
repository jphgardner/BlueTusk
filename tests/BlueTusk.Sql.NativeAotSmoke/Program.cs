using System.Data.Common;
using BlueTusk.Data;
using BlueTusk.Schema;

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString)) { throw new InvalidOperationException("Native SQL/Schema verification requires a live PostgreSQL fixture."); }
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var token = deadline.Token;
await using var dataSource = BlueTuskDataSource.Create(connectionString);
await BlueTusk.Sql.Verification.TypedShapeVerification.RunAsync(dataSource, token);
var arguments = new global::BlueTusk.Sql.NativeAotSmoke.Generated.TypedSmoke.Arguments(long.MaxValue, null, Guid.NewGuid(), "{\"answer\":42}",
    [0, 1, 128, 255], new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero), 1234567890.123456789m);
await global::BlueTusk.Sql.NativeAotSmoke.Generated.TypedSmoke.Definition.ValidateAsync(dataSource, arguments, token);
await using (var connection = await dataSource.OpenConnectionAsync(token))
{
    var count = 0;
    await foreach (var row in global::BlueTusk.Sql.NativeAotSmoke.Generated.TypedSmoke.Definition.ReadAsync(connection, arguments, cancellationToken: token))
    {
        if (row.Id != arguments.identifier || row.Label is not null || row.Key != arguments.key ||
            !row.Data.Contains("42", StringComparison.Ordinal) || !row.Blob.AsSpan().SequenceEqual(arguments.blob) ||
            row.Time != arguments.time || row.Amount != arguments.amount)
        {
            throw new InvalidOperationException("Generated native SQL bindings or row decoding changed values.");
        }
        count++;
    }
    if (count != 1) { throw new InvalidOperationException("Native SQL produced an unexpected row count."); }
}

var preciseValue = BlueTusk.TypeSystem.BlueTuskNumeric.Parse("1234567890123456789012345678901234567890.000000000001");
var collectionArguments = new global::BlueTusk.Sql.NativeAotSmoke.Generated.TypedCollections.Arguments([int.MinValue, 0, int.MaxValue], ["a,b", null, "🐘"],
    [1, null, -1], preciseValue, null, new int[,] { { 1, 2 }, { 3, 4 } });
await global::BlueTusk.Sql.NativeAotSmoke.Generated.TypedCollections.Definition.ValidateAsync(dataSource, collectionArguments, token);
await using (var collectionConnection = await dataSource.OpenConnectionAsync(token))
{
    var collectionRows = 0;
    await foreach (var row in global::BlueTusk.Sql.NativeAotSmoke.Generated.TypedCollections.Definition.ReadAsync(collectionConnection, collectionArguments, cancellationToken: token))
    {
        if (!row.Numbers.SequenceEqual(collectionArguments.numbers) || !row.Texts.SequenceEqual(collectionArguments.texts) ||
            !row.NullableNumbers.SequenceEqual(collectionArguments.nullableNumbers) || row.Precise != preciseValue || row.WholeNullable is not null ||
            row.Matrix.GetLength(0) != 2 || row.Matrix.GetLength(1) != 2 || !row.Matrix.Cast<int>().SequenceEqual(collectionArguments.matrix.Cast<int>()))
        { throw new InvalidOperationException("Native typed collections or arbitrary-precision numeric changed values/shape/nulls."); }
        collectionRows++;
    }
    if (collectionRows != 1) { throw new InvalidOperationException("Native collection result missing."); }
}

var schema = "sql_aot_" + Guid.NewGuid().ToString("N");
try
{
    await ExecuteAsync(dataSource, $"""
        CREATE SCHEMA "{schema}";
        CREATE TABLE "{schema}".items (id bigint PRIMARY KEY, label text, amount numeric);
        CREATE TYPE "{schema}".state AS ENUM ('new','done');
        CREATE DOMAIN "{schema}".positive AS numeric CHECK (VALUE >= 0);
        CREATE FUNCTION "{schema}".increment(value integer) RETURNS integer LANGUAGE SQL IMMUTABLE AS 'SELECT value + 1';
        """, token);
    var capture = new PostgreSqlSchemaCapture(dataSource, new() { Schemas = [schema] });
    var before = await capture.CaptureAsync(token);
    var restored = SchemaSnapshotSerializer.Deserialize(SchemaSnapshotSerializer.Serialize(before));
    if (restored.Fingerprint != before.Fingerprint || restored.Relations.Count != 1 || restored.Relations[0].Columns.Count != 3)
    {
        throw new InvalidOperationException("Native schema capture or JSON contract verification failed.");
    }
    await ExecuteAsync(dataSource, $"ALTER TABLE \"{schema}\".items ADD COLUMN optional text", token);
    var after = await capture.CaptureAsync(token);
    var comparison = SchemaCompatibility.Compare(before, after);
    if (comparison.HasIncompatibleChanges || !comparison.Changes.Any(change => change.Kind == SchemaChangeKind.ColumnAdded && change.Member == "optional"))
    {
        throw new InvalidOperationException("Native schema compatibility classification failed.");
    }
    var catalogue = new PostgreSqlSchemaCatalogCapture(dataSource, new() { Relations = new() { Schemas = [schema] } });
    var catalogBefore = await catalogue.CaptureAsync(token);
    var restoredCatalog = SchemaCatalogSerializer.Deserialize(SchemaCatalogSerializer.Serialize(catalogBefore));
    if (restoredCatalog.Fingerprint != catalogBefore.Fingerprint || restoredCatalog.Types.Count != 2 || restoredCatalog.Routines.Count != 1)
    { throw new InvalidOperationException("Native attested catalogue capture or source-generated envelope changed contracts."); }
    await ExecuteAsync(dataSource, $"ALTER TYPE \"{schema}\".state ADD VALUE 'pending' BEFORE 'done'", token);
    var catalogAfter = await catalogue.CaptureAsync(token);
    var catalogComparison = SchemaCatalogCompatibility.Compare(catalogBefore, catalogAfter);
    if (catalogBefore.Relations.Fingerprint != catalogAfter.Relations.Fingerprint || catalogBefore.Fingerprint == catalogAfter.Fingerprint ||
        !catalogComparison.RequiresReview || catalogComparison.HasIncompatibleChanges)
    { throw new InvalidOperationException("Native enum drift was not classified independently of relation format."); }
    var plan = SchemaDeploymentPlan.Create(catalogBefore, catalogAfter,
        [new("native-projection", [new("type", new(schema, "state"))])]);
    var progress = plan.Begin();
    while (!progress.IsComplete)
    {
        var ready = progress.ReadySteps;
        if (ready.Count == 0) { throw new InvalidOperationException("Native deployment graph contains a cycle."); }
        var step = ready[0];
        progress = progress.CompleteStep(step.Id, step.Phase switch
        {
            SchemaDeploymentPhase.VerifyBaseline => catalogBefore.Fingerprint,
            SchemaDeploymentPhase.VerifyTarget => catalogAfter.Fingerprint,
            SchemaDeploymentPhase.ApproveReview => plan.Fingerprint,
            _ => null,
        });
    }
}
finally
{
    await ExecuteAsync(dataSource, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", CancellationToken.None);
}
Console.WriteLine("SQL and Schema native live verification passed: typed values, read-only validation, attested catalogue capture, bounded JSON, enum/domain/routine contracts and deployment sequencing.");

static async ValueTask ExecuteAsync(DbDataSource dataSource, string sql, CancellationToken cancellationToken)
{
    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 30;
    _ = await command.ExecuteNonQueryAsync(cancellationToken);
}

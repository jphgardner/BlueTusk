using System.Globalization;
using System.Text;
using BlueTusk.Benchmarks.ExpansionCapacity;
using BlueTusk.Data;

namespace BlueTusk.Schema.LoadHarness;

internal static class Program
{
    private static Task<int> Main(string[] args) =>
        CapacityHost.RunAsync(args, "Schema", static (context, cancellationToken) => SchemaWorkload.CreateAsync(context, cancellationToken));
}

// Sustained Schema hot path: repeated read-only relation and catalogue captures of eight live
// application modules while fixed-cardinality DML keeps the database changing. Every capture
// must reproduce the module's baseline fingerprint.
internal sealed class SchemaWorkload : CapacityWorkload
{
    private readonly BlueTuskDataSource _source;
    private readonly string[] _modules;
    private readonly int _tablesPerModule;
    private readonly int _rowsPerTable;
    private readonly SchemaSnapshot[] _relationBaselines;
    private readonly SchemaCatalogSnapshot[] _catalogBaselines;
    private readonly int _relationWorkers;
    private readonly int _catalogWorkers;
    private readonly int _writeWorkers;
    private long _verifiedRelationCaptures;
    private long _verifiedCatalogCaptures;
    private long _applicationUpdates;

    private SchemaWorkload(CapacityContext context, BlueTuskDataSource source)
    {
        _source = source;
        var prefix = "schema_capacity_" + Guid.NewGuid().ToString("N")[..12];
        _modules = [.. Enumerable.Range(0, context.IntParameter("modules")).Select(module => $"{prefix}_m{module}")];
        _tablesPerModule = context.IntParameter("tablesPerModule");
        _rowsPerTable = context.IntParameter("rowsPerTable");
        _relationBaselines = new SchemaSnapshot[_modules.Length];
        _catalogBaselines = new SchemaCatalogSnapshot[_modules.Length];
        _relationWorkers = context.Schedule("capture-relations").Workers;
        _catalogWorkers = context.Schedule("capture-catalog").Workers;
        _writeWorkers = context.Schedule("application-write").Workers;
    }

    public static async Task<CapacityWorkload> CreateAsync(CapacityContext context, CancellationToken cancellationToken)
    {
        var settings = new BlueTuskConnectionStringBuilder(context.ConnectionString)
        {
            MaximumPoolSize = context.IntParameter("maximumPoolSize"),
            ApplicationName = "bluetusk-schema-capacity",
        };
        var workload = new SchemaWorkload(context, BlueTuskDataSource.Create(settings.ConnectionString));
        try
        {
            foreach (var module in workload._modules)
            {
                await using var command = workload._source.CreateCommand(workload.ModuleDdl(module));
                _ = await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var analyze = workload._source.CreateCommand("ANALYZE"))
            {
                _ = await analyze.ExecuteNonQueryAsync(cancellationToken);
            }

            for (var module = 0; module < workload._modules.Length; module++)
            {
                workload._relationBaselines[module] = await workload.RelationCapture(module).CaptureAsync(cancellationToken);
                workload._catalogBaselines[module] = await workload.CatalogCapture(module).CaptureAsync(cancellationToken);
                CapacityHost.Check(workload._relationBaselines[module].Relations.Count > workload._tablesPerModule,
                    "The Schema baseline capture omitted application relations.");
            }

            return workload;
        }
        catch
        {
            await workload.DisposeAsync();
            throw;
        }
    }

    private string ModuleDdl(string module)
    {
        var sql = new StringBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"""
            CREATE SCHEMA "{module}";
            CREATE TYPE "{module}".order_state AS ENUM ('new', 'paid', 'shipped', 'closed');
            CREATE DOMAIN "{module}".positive_amount AS numeric(12,2) CHECK (VALUE >= 0);
            """);
        for (var table = 0; table < _tablesPerModule; table++)
        {
            var parent = table == 0 ? string.Empty : $"""parent_id bigint NULL REFERENCES "{module}".t{table - 1:D2}(id),""";
            var parentColumn = table == 0 ? string.Empty : "parent_id, ";
            var parentValue = table == 0 ? string.Empty : "g, ";
            sql.Append(CultureInfo.InvariantCulture, $"""

                CREATE TABLE "{module}".t{table:D2} (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    tenant_id text NOT NULL,
                    {parent}
                    state "{module}".order_state NOT NULL DEFAULT 'new',
                    amount "{module}".positive_amount NOT NULL DEFAULT 0,
                    quantity integer NOT NULL CHECK (quantity >= 0),
                    reference uuid NOT NULL,
                    attributes jsonb NOT NULL DEFAULT jsonb_build_object(),
                    note text NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now(),
                    version bigint NOT NULL DEFAULT 1,
                    CONSTRAINT t{table:D2}_reference_unique UNIQUE (tenant_id, reference));
                CREATE INDEX t{table:D2}_tenant_created ON "{module}".t{table:D2} (tenant_id, created_at);
                INSERT INTO "{module}".t{table:D2} (tenant_id, {(table == 0 ? string.Empty : "parent_id, ")}quantity, reference, note)
                    SELECT 'tenant-' || (g % 8), {parentValue}g % 100, gen_random_uuid(), repeat('n', 64)
                    FROM generate_series(1, {_rowsPerTable}) AS g;
                """);
            if (table % 4 == 0)
            {
                sql.Append(CultureInfo.InvariantCulture, $"""

                    ALTER TABLE "{module}".t{table:D2} ENABLE ROW LEVEL SECURITY;
                    CREATE POLICY t{table:D2}_tenant ON "{module}".t{table:D2} USING (tenant_id = current_setting('app.tenant', true));
                    """);
            }
        }

        sql.Append(CultureInfo.InvariantCulture, $"""

            CREATE VIEW "{module}".open_orders AS SELECT id, tenant_id, amount FROM "{module}".t00 WHERE state <> 'closed';
            CREATE FUNCTION "{module}".touch(p_id bigint) RETURNS void LANGUAGE sql
                AS $touch$ UPDATE "{module}".t00 SET version = version + 1 WHERE id = p_id $touch$;
            """);
        return sql.ToString();
    }

    private PostgreSqlSchemaCapture RelationCapture(int module, int maximumRelations = 10_000) =>
        new(_source, new SchemaCaptureOptions { Schemas = [_modules[module]], MaximumRelations = maximumRelations });

    private PostgreSqlSchemaCatalogCapture CatalogCapture(int module) =>
        new(_source, new SchemaCatalogCaptureOptions { Relations = new SchemaCaptureOptions { Schemas = [_modules[module]] } });

    public override IReadOnlyList<string> OwnedSchemas => _modules;

    public override IReadOnlyList<ScheduledOperation> Operations =>
    [
        new("capture-relations", CaptureRelationsAsync),
        new("capture-catalog", CaptureCatalogAsync),
        new("application-write", WriteAsync),
    ];

    private async ValueTask<OperationOutcome> CaptureRelationsAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var module = (int)((slot * _relationWorkers + worker) % _modules.Length);
        SchemaSnapshot snapshot;
        try { snapshot = await RelationCapture(module).CaptureAsync(cancellationToken); }
        catch (SchemaConcurrentDdlException) { return OperationOutcome.Rejected; }
        CapacityHost.Check(string.Equals(snapshot.Fingerprint, _relationBaselines[module].Fingerprint, StringComparison.Ordinal),
            "A relation capture of an unchanged module produced a different fingerprint.");
        Interlocked.Increment(ref _verifiedRelationCaptures);
        return OperationOutcome.Accepted;
    }

    private async ValueTask<OperationOutcome> CaptureCatalogAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var module = (int)((slot * _catalogWorkers + worker) % _modules.Length);
        SchemaCatalogSnapshot snapshot;
        try { snapshot = await CatalogCapture(module).CaptureAsync(cancellationToken); }
        catch (SchemaConcurrentDdlException) { return OperationOutcome.Rejected; }
        CapacityHost.Check(string.Equals(snapshot.Fingerprint, _catalogBaselines[module].Fingerprint, StringComparison.Ordinal),
            "A catalogue capture of an unchanged module produced a different fingerprint.");
        Interlocked.Increment(ref _verifiedCatalogCaptures);
        return OperationOutcome.Accepted;
    }

    private async ValueTask<OperationOutcome> WriteAsync(int worker, long slot, CancellationToken cancellationToken)
    {
        var ordinal = slot * _writeWorkers + worker;
        var module = _modules[(int)(ordinal % _modules.Length)];
        var table = (int)(ordinal / _modules.Length % _tablesPerModule);
        var id = ordinal / ((long)_modules.Length * _tablesPerModule) % _rowsPerTable + 1;
        await using var command = _source.CreateCommand(string.Create(CultureInfo.InvariantCulture, $"""
            UPDATE "{module}".t{table:D2} SET amount = amount + 1, quantity = quantity + 1, updated_at = now(),
                version = version + 1, note = md5(version::text) WHERE id = {id}
            """));
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        CapacityHost.Check(updated == 1, "Fixed-cardinality application DML did not update exactly one row.");
        Interlocked.Increment(ref _applicationUpdates);
        return OperationOutcome.Accepted;
    }

    public override async Task DrainAndVerifyAsync(CapacityRecorder recorder, CancellationToken cancellationToken)
    {
        recorder.Counter("verifiedRelationCaptures", Interlocked.Read(ref _verifiedRelationCaptures));
        recorder.Counter("verifiedCatalogCaptures", Interlocked.Read(ref _verifiedCatalogCaptures));
        recorder.Counter("applicationUpdates", Interlocked.Read(ref _applicationUpdates));
        recorder.Counter("relationsPerModule", _relationBaselines[0].Relations.Count);
        recorder.Invariant("deterministic-relation-fingerprints", Interlocked.Read(ref _verifiedRelationCaptures) > 0,
            $"Every one of {Interlocked.Read(ref _verifiedRelationCaptures)} accepted relation captures matched its module baseline.");
        recorder.Invariant("deterministic-catalog-fingerprints", Interlocked.Read(ref _verifiedCatalogCaptures) > 0,
            $"Every one of {Interlocked.Read(ref _verifiedCatalogCaptures)} accepted catalogue captures matched its module baseline.");

        var roundTrips = 0;
        var unchanged = 0;
        for (var module = 0; module < _modules.Length; module++)
        {
            var relations = await RelationCapture(module).CaptureAsync(cancellationToken);
            var catalog = await CatalogCapture(module).CaptureAsync(cancellationToken);
            if (SchemaSnapshotSerializer.Deserialize(SchemaSnapshotSerializer.Serialize(relations)).Fingerprint == relations.Fingerprint &&
                SchemaCatalogSerializer.Deserialize(SchemaCatalogSerializer.Serialize(catalog)).Fingerprint == catalog.Fingerprint)
            {
                roundTrips++;
            }

            var relationComparison = SchemaCompatibility.Compare(_relationBaselines[module], relations);
            var catalogComparison = SchemaCatalogCompatibility.Compare(_catalogBaselines[module], catalog);
            if (relationComparison.Changes.Count == 0 && !relationComparison.HasIncompatibleChanges &&
                catalogComparison.Changes.Count == 0 && !catalogComparison.HasIncompatibleChanges)
            {
                unchanged++;
            }
        }

        recorder.Invariant("serialization-round-trip", roundTrips == _modules.Length, $"modules={roundTrips}/{_modules.Length}");
        recorder.Invariant("compare-reports-no-change", unchanged == _modules.Length, $"modules={unchanged}/{_modules.Length}");

        var limitRejected = false;
        try { _ = await RelationCapture(0, maximumRelations: 8).CaptureAsync(cancellationToken); }
        catch (SchemaCaptureLimitException) { limitRejected = true; }
        recorder.Invariant("relation-limit-fails-closed", limitRejected, "A capture beyond MaximumRelations must fail instead of returning a partial snapshot.");

        long rows = 0;
        foreach (var module in _modules)
        {
            var union = string.Join(" UNION ALL ", Enumerable.Range(0, _tablesPerModule)
                .Select(table => string.Create(CultureInfo.InvariantCulture, $"SELECT count(*) AS c FROM \"{module}\".t{table:D2}")));
            await using var command = _source.CreateCommand($"SELECT sum(c)::bigint FROM ({union}) AS counts");
            rows += Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        var expected = (long)_modules.Length * _tablesPerModule * _rowsPerTable;
        recorder.Invariant("application-rows-exact", rows == expected, $"rows={rows}, expected={expected}");
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var module in _modules)
            {
                await using var command = _source.CreateCommand($"DROP SCHEMA IF EXISTS \"{module}\" CASCADE");
                _ = await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await _source.DisposeAsync();
        }
    }
}

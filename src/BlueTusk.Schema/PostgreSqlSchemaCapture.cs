using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace BlueTusk.Schema;

public sealed record SchemaCaptureOptions
{
    public required IReadOnlyList<string> Schemas { get; init; }
    public int MaximumRelations { get; init; } = 10_000;
    public int MaximumColumns { get; init; } = 200_000;
    public int MaximumConstraints { get; init; } = 100_000;
    public int MaximumIndexes { get; init; } = 100_000;
    public int MaximumPolicies { get; init; } = 100_000;
    public long MaximumMetadataBytes { get; init; } = 64 * 1024 * 1024;
    public int CommandTimeoutSeconds { get; init; } = 30;
}

public sealed class SchemaCaptureLimitException : InvalidOperationException
{
    public SchemaCaptureLimitException() : base("The catalogue snapshot exceeds its configured metadata bound.")
    {
    }
}

/// <summary>Captures a consistent read-only catalogue view. The caller owns the data source.</summary>
public sealed class PostgreSqlSchemaCapture
{
    private readonly string _relationFilter;
    private readonly DbDataSource _dataSource;
    private readonly SchemaCaptureOptions _options;
    private readonly string[] _schemas;
    private readonly SchemaSnapshotLimits _modelLimits;

    public PostgreSqlSchemaCapture(DbDataSource dataSource, SchemaCaptureOptions options) : this(dataSource, options, null) { }

    internal PostgreSqlSchemaCapture(DbDataSource dataSource, SchemaCaptureOptions options, SchemaSnapshotLimits? modelLimits)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Schemas);
        if (options.Schemas.Count is < 1 or > 64)
        {
            throw new ArgumentException("Select between one and 64 valid schema names.", nameof(options));
        }
        var schemaBudget = new SchemaModelBudget(SchemaSnapshotLimits.Default);
        var selectedSchemas = new List<string>();
        foreach (var schema in options.Schemas)
        {
            if (selectedSchemas.Count == 64) { throw new ArgumentException("Select at most 64 schema names.", nameof(options)); }
            schemaBudget.Text(schema, required: true, identifier: true);
            selectedSchemas.Add(schema);
        }
        if (selectedSchemas.Count == 0) { throw new ArgumentException("At least one schema name is required.", nameof(options)); }
        _schemas = selectedSchemas.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumRelations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumRelations, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumColumns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumColumns, 2_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumConstraints, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumConstraints, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumIndexes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumIndexes, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumPolicies, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumPolicies, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumMetadataBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumMetadataBytes, 1024L * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CommandTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.CommandTimeoutSeconds, 300);
        _options = options;
        _modelLimits = modelLimits ?? new()
        {
            MaximumRelations = options.MaximumRelations,
            MaximumColumns = options.MaximumColumns,
            MaximumConstraints = options.MaximumConstraints,
            MaximumIndexes = options.MaximumIndexes,
            MaximumPolicies = options.MaximumPolicies,
            MaximumMetadataBytes = checked((int)options.MaximumMetadataBytes),
        };
        _modelLimits.Validate();
        _dataSource = dataSource;
        var parameters = string.Join(",", Enumerable.Range(0, _schemas.Length).Select(index => "@schema" + index.ToString(CultureInfo.InvariantCulture)));
        _relationFilter = $"n.nspname IN ({parameters}) AND c.relkind IN ('r','p','v','m','f')";
    }

    public async ValueTask<SchemaSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await using (var setup = connection.CreateCommand())
        {
            setup.Transaction = transaction;
            setup.CommandTimeout = _options.CommandTimeoutSeconds;
            setup.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL search_path = pg_catalog";
            _ = await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await CaptureCoreAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>Captures in a caller-owned, read-only repeatable-read or serializable transaction; neither commits nor changes its search path.</summary>
    public async ValueTask<SchemaSnapshot> CaptureInTransactionAsync(DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection) ||
            transaction.IsolationLevel is not (IsolationLevel.RepeatableRead or IsolationLevel.Serializable))
        { throw new ArgumentException("A caller-owned consistent transaction is required.", nameof(transaction)); }
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandTimeout = _options.CommandTimeoutSeconds;
        check.CommandText = "SELECT pg_catalog.current_setting('transaction_read_only') = 'on'";
        if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        { throw new ArgumentException("A read-only transaction is required.", nameof(transaction)); }
        return await CaptureCoreAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SchemaSnapshot> CaptureCoreAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var budget = new MetadataBudget(_options.MaximumMetadataBytes);
        var relations = new Dictionary<SchemaRelationIdentity, RelationBuilder>();
        await ReadAsync(connection, transaction, $"""
            SELECT n.nspname::text, c.relname::text, c.relkind::text, c.relrowsecurity,
                c.relforcerowsecurity, c.relreplident::text,
                CASE WHEN c.relkind IN ('v','m') THEN pg_catalog.pg_get_viewdef(c.oid, false)
                    WHEN c.relkind = 'p' THEN pg_catalog.jsonb_build_object('partitionKey',pg_catalog.pg_get_partkeydef(c.oid),
                        'partitionBound',pg_catalog.pg_get_expr(c.relpartbound,c.oid))::text
                    ELSE pg_catalog.pg_get_expr(c.relpartbound, c.oid) END
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE {_relationFilter}
            ORDER BY n.nspname COLLATE "C", c.relname COLLATE "C" LIMIT @limit
            """, 7, [0, 1, 2, 5, 6], _options.MaximumRelations, reader =>
            {
                budget.Add(128);
                var identity = Identity(reader, budget);
                relations.Add(identity, new RelationBuilder(identity, Text(reader, 2, budget), reader.GetBoolean(3),
                    reader.GetBoolean(4), Text(reader, 5, budget), NullableText(reader, 6, budget)));
            }, cancellationToken).ConfigureAwait(false);

        await ReadAsync(connection, transaction, $"""
            SELECT n.nspname::text, c.relname::text, a.attname::text, a.attnum::int4,
                pg_catalog.format_type(a.atttypid, a.atttypmod), NOT a.attnotnull,
                pg_catalog.pg_get_expr(d.adbin, d.adrelid), a.attidentity::text, a.attgenerated::text,
                CASE WHEN a.attcollation <> 0 THEN pg_catalog.quote_ident(cn.nspname) || '.' || pg_catalog.quote_ident(co.collname) END
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
            LEFT JOIN pg_catalog.pg_collation co ON co.oid = a.attcollation
            LEFT JOIN pg_catalog.pg_namespace cn ON cn.oid = co.collnamespace
            WHERE {_relationFilter} AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY n.nspname COLLATE "C", c.relname COLLATE "C", a.attnum LIMIT @limit
            """, 10, [0, 1, 2, 4, 6, 7, 8, 9], _options.MaximumColumns, reader =>
            {
                budget.Add(64);
                var relation = relations[Identity(reader, budget)];
                relation.Columns.Add(new(Text(reader, 2, budget), reader.GetInt32(3), Text(reader, 4, budget),
                    reader.GetBoolean(5), NullableText(reader, 6, budget), Text(reader, 7, budget),
                    Text(reader, 8, budget), NullableText(reader, 9, budget)));
            }, cancellationToken).ConfigureAwait(false);

        await ReadAsync(connection, transaction, $"""
            SELECT n.nspname::text, c.relname::text, k.conname::text, k.contype::text,
                pg_catalog.pg_get_constraintdef(k.oid, false)
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_constraint k ON k.conrelid = c.oid
            WHERE {_relationFilter}
            ORDER BY n.nspname COLLATE "C", c.relname COLLATE "C", k.conname COLLATE "C" LIMIT @limit
            """, 5, [0, 1, 2, 3, 4], _options.MaximumConstraints, reader =>
            {
                budget.Add(32); relations[Identity(reader, budget)].Constraints.Add(new(Text(reader, 2, budget), Text(reader, 3, budget), Text(reader, 4, budget)));
            }, cancellationToken).ConfigureAwait(false);

        await ReadAsync(connection, transaction, $"""
            SELECT n.nspname::text, c.relname::text, ic.relname::text,
                pg_catalog.pg_get_indexdef(i.indexrelid), i.indisvalid
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_index i ON i.indrelid = c.oid
            JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid
            WHERE {_relationFilter}
            ORDER BY n.nspname COLLATE "C", c.relname COLLATE "C", ic.relname COLLATE "C" LIMIT @limit
            """, 5, [0, 1, 2, 3], _options.MaximumIndexes, reader =>
            {
                budget.Add(32); relations[Identity(reader, budget)].Indexes.Add(new(Text(reader, 2, budget), Text(reader, 3, budget), reader.GetBoolean(4)));
            }, cancellationToken).ConfigureAwait(false);

        await ReadAsync(connection, transaction, $"""
            SELECT n.nspname::text, c.relname::text, p.polname::text, p.polcmd::text, p.polpermissive,
                (SELECT pg_catalog.string_agg(CASE WHEN role_oid = 0 THEN 'PUBLIC' ELSE pg_catalog.quote_ident(r.rolname) END, ',' ORDER BY r.rolname COLLATE "C")
                    FROM pg_catalog.unnest(p.polroles) role_oid LEFT JOIN pg_catalog.pg_roles r ON r.oid = role_oid),
                pg_catalog.pg_get_expr(p.polqual, p.polrelid), pg_catalog.pg_get_expr(p.polwithcheck, p.polrelid)
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_policy p ON p.polrelid = c.oid
            WHERE {_relationFilter}
            ORDER BY n.nspname COLLATE "C", c.relname COLLATE "C", p.polname COLLATE "C" LIMIT @limit
            """, 8, [0, 1, 2, 3, 5, 6, 7], _options.MaximumPolicies, reader =>
            {
                budget.Add(64); relations[Identity(reader, budget)].Policies.Add(new(Text(reader, 2, budget), Text(reader, 3, budget),
                    reader.GetBoolean(4), Text(reader, 5, budget), NullableText(reader, 6, budget), NullableText(reader, 7, budget)));
            }, cancellationToken).ConfigureAwait(false);

        var snapshot = new SchemaSnapshot(relations.Values.Select(relation => relation.Build(_modelLimits)), _modelLimits);
        return snapshot;
    }

    private async Task ReadAsync(DbConnection connection, DbTransaction transaction, string sql, int fields, int[] textFields, int maximum,
        Action<DbDataReader> accept, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        var names = Enumerable.Range(0, fields).Select(value => "c" + value.ToString(CultureInfo.InvariantCulture)).ToArray();
        var texts = textFields.ToHashSet();
        var selected = names.Select((name, index) => texts.Contains(index)
            ? $"CASE WHEN pg_catalog.octet_length(pg_catalog.convert_to(q.{name},'UTF8')) <= @textlimit THEN q.{name} END" : "q." + name);
        var overflow = string.Join(" OR ", textFields.Select(index => $"COALESCE(pg_catalog.octet_length(pg_catalog.convert_to(q.{names[index]},'UTF8')) > @textlimit, false)"));
        command.CommandText = "SELECT " + string.Join(',', selected) + ",(" + overflow + ") FROM (" + sql + ") q(" + string.Join(',', names) + ")";
        for (var index = 0; index < _schemas.Length; index++)
        {
            var schema = command.CreateParameter();
            schema.ParameterName = "schema" + index.ToString(CultureInfo.InvariantCulture);
            schema.DbType = DbType.String;
            schema.Value = _schemas[index];
            command.Parameters.Add(schema);
        }
        var limit = command.CreateParameter();
        limit.ParameterName = "limit";
        limit.DbType = DbType.Int32;
        limit.Value = maximum + 1;
        command.Parameters.Add(limit);
        var textLimit = command.CreateParameter();
        textLimit.ParameterName = "textlimit"; textLimit.DbType = DbType.Int32;
        textLimit.Value = Math.Min(_modelLimits.MaximumStringBytes, checked((int)_options.MaximumMetadataBytes));
        command.Parameters.Add(textLimit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (++count > maximum || reader.GetBoolean(fields))
            {
                throw new SchemaCaptureLimitException();
            }

            accept(reader);
        }
    }

    private static SchemaRelationIdentity Identity(DbDataReader reader, MetadataBudget budget) => new(Text(reader, 0, budget), Text(reader, 1, budget));
    private static string? NullableText(DbDataReader reader, int ordinal, MetadataBudget budget) => reader.IsDBNull(ordinal) ? null : Text(reader, ordinal, budget);
    private static string Text(DbDataReader reader, int ordinal, MetadataBudget budget)
    {
        var value = reader.GetString(ordinal);
        budget.Add(Encoding.UTF8.GetByteCount(value));
        return value;
    }

    private sealed class MetadataBudget(long maximum)
    {
        private long _used;

        public void Add(int count)
        {
            _used += count;
            if (_used > maximum)
            {
                throw new SchemaCaptureLimitException();
            }
        }
    }

    private sealed class RelationBuilder(SchemaRelationIdentity identity, string kind, bool rowSecurity,
        bool forceRowSecurity, string replicaIdentity, string? definitionSql)
    {
        public List<SchemaColumn> Columns { get; } = [];
        public List<SchemaConstraint> Constraints { get; } = [];
        public List<SchemaIndex> Indexes { get; } = [];
        public List<SchemaPolicy> Policies { get; } = [];
        public SchemaRelation Build(SchemaSnapshotLimits limits) => new(identity, kind, rowSecurity, forceRowSecurity, replicaIdentity,
            Columns, Constraints, Indexes, Policies, definitionSql, limits);
    }
}

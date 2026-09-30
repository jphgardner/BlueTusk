using System.Data;
using System.Data.Common;
using System.Globalization;

namespace BlueTusk.Schema;

public sealed record SchemaCatalogCaptureOptions
{
    public required SchemaCaptureOptions Relations { get; init; }
    public SchemaCatalogLimits Limits { get; init; } = new();
}

public sealed class SchemaConcurrentDdlException : InvalidOperationException
{
    public SchemaConcurrentDdlException() : base("Catalogue definitions or the verification source changed during capture; start a fresh capture.") { }
}

/// <summary>Captures relation and supplemental contracts in the same read-only PostgreSQL snapshot.</summary>
public sealed class PostgreSqlSchemaCatalogCapture
{
    private readonly DbDataSource _dataSource;
    private readonly PostgreSqlSchemaCapture _relations;
    private readonly SchemaCatalogLimits _limits;
    private readonly string[] _schemas;
    private readonly string _filter;
    private readonly int _timeout;
    private readonly SchemaCatalogCaptureOptions _options;

    public PostgreSqlSchemaCatalogCapture(DbDataSource dataSource, SchemaCatalogCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(dataSource); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Relations); ArgumentNullException.ThrowIfNull(options.Limits);
        options.Limits.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Limits.MaximumMetadataBytes, 1024);
        ArgumentNullException.ThrowIfNull(options.Relations.Schemas);
        var schemaBudget = new SchemaModelBudget(SchemaSnapshotLimits.Default);
        _schemas = SchemaModelBudget.Materialize(options.Relations.Schemas, 64,
            value => schemaBudget.Text(value, required: true, identifier: true)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var relationOptions = options.Relations with
        {
            Schemas = _schemas,
            MaximumRelations = Math.Min(options.Relations.MaximumRelations, options.Limits.Relations.MaximumRelations),
            MaximumColumns = Math.Min(options.Relations.MaximumColumns, options.Limits.Relations.MaximumColumns),
            MaximumConstraints = Math.Min(options.Relations.MaximumConstraints, options.Limits.Relations.MaximumConstraints),
            MaximumIndexes = Math.Min(options.Relations.MaximumIndexes, options.Limits.Relations.MaximumIndexes),
            MaximumPolicies = Math.Min(options.Relations.MaximumPolicies, options.Limits.Relations.MaximumPolicies),
            MaximumMetadataBytes = Math.Min(Math.Min(options.Relations.MaximumMetadataBytes, options.Limits.MaximumMetadataBytes), options.Limits.Relations.MaximumMetadataBytes),
        };
        _relations = new PostgreSqlSchemaCapture(dataSource, relationOptions, options.Limits.Relations with
        {
            MaximumRelations = relationOptions.MaximumRelations,
            MaximumColumns = relationOptions.MaximumColumns,
            MaximumConstraints = relationOptions.MaximumConstraints,
            MaximumIndexes = relationOptions.MaximumIndexes,
            MaximumPolicies = relationOptions.MaximumPolicies,
            MaximumMetadataBytes = checked((int)relationOptions.MaximumMetadataBytes),
        });
        _options = options with { Relations = relationOptions };
        _dataSource = dataSource; _limits = options.Limits; _timeout = options.Relations.CommandTimeoutSeconds;
        _filter = "n.nspname IN (" + string.Join(',', Enumerable.Range(0, _schemas.Length)
            .Select(index => "@schema" + index.ToString(CultureInfo.InvariantCulture))) + ")";
    }

    public async ValueTask<SchemaCatalogSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_timeout));
        var token = deadline.Token;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
                await using (var setup = connection.CreateCommand())
                {
                    setup.Transaction = transaction; setup.CommandTimeout = _timeout;
                    setup.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL search_path = pg_catalog";
                    _ = await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                var result = await CaptureInTransactionAsync(connection, transaction, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return result;
            }
            catch (SchemaConcurrentDdlException) when (attempt < 2)
            { await Task.Delay(TimeSpan.FromMilliseconds(75 * (attempt + 1)), token).ConfigureAwait(false); }
        }
    }

    /// <summary>Uses a caller-owned consistent read-only transaction; does not commit or change its settings.</summary>
    public async ValueTask<SchemaCatalogSnapshot> CaptureInTransactionAsync(DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        using var attestation = await PostgreSqlCatalogConsistencyAttestation.BeginAsync(_dataSource, connection, transaction,
            _options, cancellationToken).ConfigureAwait(false);
        var result = await CaptureCoreAsync(connection, transaction, attestation.CancellationToken).ConfigureAwait(false);
        try { await attestation.VerifyAsync().ConfigureAwait(false); }
        catch (SchemaCatalogAttestationMismatchException) { throw new SchemaConcurrentDdlException(); }
        return result;
    }

    private async ValueTask<SchemaCatalogSnapshot> CaptureCoreAsync(DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var relations = await _relations.CaptureInTransactionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using (var settings = connection.CreateCommand())
        {
            settings.Transaction = transaction; settings.CommandTimeout = _timeout;
            settings.CommandText = "SELECT pg_catalog.current_setting('search_path') = 'pg_catalog'";
            if (await settings.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            { throw new ArgumentException("Catalogue contracts require an explicit pg_catalog search path.", nameof(transaction)); }
        }
        var budget = _limits.Budget();
        foreach (var relation in relations.Relations) { budget.Add(relation.MetadataBytes); }
        var typeBuilders = new Dictionary<SchemaRelationIdentity, TypeBuilder>();
        var routines = new List<SchemaRoutineContract>();
        var privileges = new List<SchemaPrivilegeContract>();
        var publications = new List<SchemaPublicationMemberContract>();
        var extensions = new List<SchemaExtensionContract>();
        var entryCount = 0;
        void Entry() { if (++entryCount > _limits.MaximumEntries) { throw new SchemaCaptureLimitException(); } budget.Add(96); }
        string Text(DbDataReader reader, int ordinal)
        { var value = reader.GetString(ordinal); budget.Text(value); return value; }
        string? Optional(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Text(reader, ordinal);
        SchemaRelationIdentity Identity(DbDataReader reader, int ordinal = 0) => new(Text(reader, ordinal), Text(reader, ordinal + 1));

        await ReadAsync(connection, transaction, $"""
            SELECT n.nspname::text, t.typname::text, t.typtype::text,
                CASE WHEN t.typtype = 'd' THEN pg_catalog.format_type(t.typbasetype, t.typtypmod) END,
                NOT t.typnotnull, t.typdefault,
                CASE WHEN t.typtype = 'd' AND t.typcollation <> 0 THEN pg_catalog.quote_ident(cn.nspname) || '.' || pg_catalog.quote_ident(c.collname) END
            FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
            LEFT JOIN pg_catalog.pg_collation c ON c.oid = t.typcollation
            LEFT JOIN pg_catalog.pg_namespace cn ON cn.oid = c.collnamespace
            WHERE {_filter} AND t.typtype IN ('e','d')
            ORDER BY n.nspname COLLATE "C", t.typname COLLATE "C" LIMIT @limit
            """, 7, [0, 1, 2, 3, 5, 6], _limits.MaximumEntries, reader =>
        {
            Entry(); var identity = Identity(reader);
            typeBuilders.Add(identity, new(identity, Text(reader, 2), Optional(reader, 3), reader.GetBoolean(4), Optional(reader, 5), Optional(reader, 6)));
        }, cancellationToken).ConfigureAwait(false);

        await ReadAsync(connection, transaction, $"""
            SELECT n.nspname::text, t.typname::text, e.enumlabel::text,
                (pg_catalog.row_number() OVER (PARTITION BY t.oid ORDER BY e.enumsortorder))::int4
            FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_catalog.pg_enum e ON e.enumtypid = t.oid WHERE {_filter}
            ORDER BY n.nspname COLLATE "C", t.typname COLLATE "C", e.enumsortorder LIMIT @limit
            """, 4, [0, 1, 2], _limits.MaximumEnumLabels, reader =>
        {
            typeBuilders[Identity(reader)].Labels.Add((reader.GetInt32(3), Text(reader, 2)));
        }, cancellationToken).ConfigureAwait(false);

        await ReadAsync(connection, transaction, $"""
            SELECT n.nspname::text, t.typname::text, c.conname::text, pg_catalog.pg_get_constraintdef(c.oid, false), c.contype::text
            FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_catalog.pg_constraint c ON c.contypid = t.oid WHERE {_filter} AND t.typtype = 'd'
            ORDER BY n.nspname COLLATE "C", t.typname COLLATE "C", c.conname COLLATE "C" LIMIT @limit
            """, 5, [0, 1, 2, 3, 4], _limits.MaximumDomainConstraints, reader =>
            typeBuilders[Identity(reader)].Constraints.Add(new(Text(reader, 2), Text(reader, 4), Text(reader, 3))), cancellationToken).ConfigureAwait(false);

        await ReadAsync(connection, transaction, $"""
            SELECT n.nspname::text, p.proname::text, pg_catalog.oidvectortypes(p.proargtypes), p.prokind::text,
                COALESCE(pg_catalog.pg_get_function_result(p.oid), ''), l.lanname::text, p.prosecdef, p.proisstrict,
                p.provolatile::text, p.proparallel::text, pg_catalog.pg_get_functiondef(p.oid)
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            JOIN pg_catalog.pg_language l ON l.oid = p.prolang
            WHERE {_filter} AND p.prokind IN ('f','p','w')
            ORDER BY n.nspname COLLATE "C", p.proname COLLATE "C", pg_catalog.oidvectortypes(p.proargtypes) COLLATE "C" LIMIT @limit
            """, 11, [0, 1, 2, 3, 4, 5, 8, 9, 10], _limits.MaximumEntries, reader =>
        {
            Entry(); routines.Add(new(Identity(reader), Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5),
                reader.GetBoolean(6), reader.GetBoolean(7), Text(reader, 8), Text(reader, 9), Text(reader, 10)));
        }, cancellationToken).ConfigureAwait(false);

        await ReadAsync(connection, transaction, $"""
            WITH objects AS (
                SELECT 'schema'::text AS kind, n.nspname::text AS schema, n.nspname::text AS name,
                    NULL::text AS member, NULL::text AS arguments, n.nspacl AS acl, n.nspowner AS owner, 'n'::"char" AS aclkind
                FROM pg_catalog.pg_namespace n WHERE {_filter}
                UNION ALL
                SELECT 'relation', n.nspname::text, c.relname::text, NULL, NULL, c.relacl, c.relowner,
                    CASE WHEN c.relkind = 'S' THEN 's'::"char" ELSE 'r'::"char" END
                FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE {_filter} AND c.relkind IN ('r','p','v','m','f','S')
                UNION ALL
                SELECT 'column', n.nspname::text, c.relname::text, a.attname::text, NULL, a.attacl, c.relowner, 'c'::"char"
                FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid
                WHERE {_filter} AND a.attnum > 0 AND NOT a.attisdropped AND a.attacl IS NOT NULL
                UNION ALL
                SELECT 'routine', n.nspname::text, p.proname::text, NULL, pg_catalog.oidvectortypes(p.proargtypes),
                    p.proacl, p.proowner, 'f'::"char"
                FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                WHERE {_filter} AND p.prokind IN ('f','p','w')
                UNION ALL
                SELECT 'type', n.nspname::text, t.typname::text, NULL, NULL, t.typacl, t.typowner, 'T'::"char"
                FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
                WHERE {_filter} AND t.typtype IN ('e','d')
                UNION ALL
                SELECT DISTINCT 'publication', n.nspname::text, p.pubname::text, NULL, NULL, NULL::pg_catalog.aclitem[], p.pubowner, 'r'::"char"
                FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                CROSS JOIN pg_catalog.pg_publication p
                LEFT JOIN pg_catalog.pg_publication_rel pr ON pr.prpubid = p.oid AND pr.prrelid = c.oid
                LEFT JOIN pg_catalog.pg_publication_namespace pn ON pn.pnpubid = p.oid AND pn.pnnspid = n.oid
                WHERE {_filter} AND c.relkind IN ('r','p') AND c.relpersistence = 'p'
                    AND (p.puballtables OR pr.oid IS NOT NULL OR pn.oid IS NOT NULL)
            )
            SELECT o.kind, o.schema, o.name, o.member, o.arguments, grantor.rolname::text,
                CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE grantee.rolname::text END, a.privilege_type, a.is_grantable, a.grantee = 0
            FROM objects o CROSS JOIN LATERAL (
                SELECT acl.grantor, acl.grantee, acl.privilege_type, acl.is_grantable
                FROM pg_catalog.aclexplode(CASE WHEN o.kind = 'publication' THEN NULL::pg_catalog.aclitem[]
                    ELSE COALESCE(o.acl, pg_catalog.acldefault(o.aclkind, o.owner)) END) acl
                UNION ALL SELECT o.owner, o.owner, 'OWNER'::text, true WHERE o.kind <> 'column'
            ) a
            JOIN pg_catalog.pg_roles grantor ON grantor.oid = a.grantor
            LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid = a.grantee
            ORDER BY o.kind COLLATE "C", o.schema COLLATE "C", o.name COLLATE "C", o.member COLLATE "C",
                o.arguments COLLATE "C", grantor.rolname COLLATE "C", grantee.rolname COLLATE "C", a.privilege_type COLLATE "C" LIMIT @limit
            """, 10, [0, 1, 2, 3, 4, 5, 6, 7], _limits.MaximumEntries, reader =>
        {
            Entry(); privileges.Add(new(Text(reader, 0), Identity(reader, 1), Optional(reader, 3), Optional(reader, 4),
                Text(reader, 5), Text(reader, 6), Text(reader, 7), reader.GetBoolean(8), reader.GetBoolean(9)));
        }, cancellationToken).ConfigureAwait(false);

        // Read configured membership from MVCC rows. pg_publication_tables uses
        // current caches, including transient add/drop rows absent at both attestation observations.
        // Partition expansion remains PostgreSQL's responsibility; the declared root is retained.
        await ReadAsync(connection, transaction, $"""
            SELECT p.pubname::text, n.nspname::text, c.relname::text, p.puballtables, p.pubviaroot,
                p.pubinsert, p.pubupdate, p.pubdelete, p.pubtruncate,
                COALESCE((SELECT pg_catalog.string_agg(pg_catalog.quote_ident(a.attname::text), ',' ORDER BY a.attnum)
                    FROM pg_catalog.pg_attribute a WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                        AND (pr.prattrs IS NULL OR a.attnum = ANY(pr.prattrs))), ''),
                pg_catalog.pg_get_expr(pr.prqual, pr.prrelid)
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN pg_catalog.pg_publication p
            LEFT JOIN pg_catalog.pg_publication_rel pr ON pr.prpubid = p.oid AND pr.prrelid = c.oid
            LEFT JOIN pg_catalog.pg_publication_namespace pn ON pn.pnpubid = p.oid AND pn.pnnspid = n.oid
            WHERE {_filter} AND c.relkind IN ('r','p') AND c.relpersistence = 'p'
                AND (p.puballtables OR pr.oid IS NOT NULL OR pn.oid IS NOT NULL)
            ORDER BY p.pubname COLLATE "C", n.nspname COLLATE "C", c.relname COLLATE "C" LIMIT @limit
            """, 11, [0, 1, 2, 9, 10], _limits.MaximumEntries, reader =>
        {
            Entry(); publications.Add(new(Text(reader, 0), Identity(reader, 1), reader.GetBoolean(3), reader.GetBoolean(4),
                reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), Text(reader, 9), Optional(reader, 10)));
        }, cancellationToken).ConfigureAwait(false);

        await ReadAsync(connection, transaction, $"""
            SELECT e.extname::text, n.nspname::text, e.extversion, e.extrelocatable
            FROM pg_catalog.pg_extension e JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace WHERE {_filter}
            ORDER BY e.extname COLLATE "C" LIMIT @limit
            """, 4, [0, 1, 2], _limits.MaximumEntries, reader =>
        {
            Entry(); extensions.Add(new(Text(reader, 0), Text(reader, 1), Text(reader, 2), reader.GetBoolean(3)));
        }, cancellationToken).ConfigureAwait(false);

        return new(relations, typeBuilders.Values.Select(value => new SchemaTypeContract(value.Identity, value.Kind,
            value.Labels.OrderBy(label => label.Ordinal).Select(label => label.Label), value.BaseType, value.IsNullable,
            value.DefaultSql, value.Collation, value.Constraints, _limits)), routines, privileges, publications, extensions, _limits);
    }

    private async Task ReadAsync(DbConnection connection, DbTransaction transaction, string sql, int fields, int[] textFields,
        int maximum, Action<DbDataReader> accept, CancellationToken cancellationToken)
    {
        var names = Enumerable.Range(0, fields).Select(value => "c" + value.ToString(CultureInfo.InvariantCulture)).ToArray();
        var texts = textFields.ToHashSet();
        var selected = names.Select((name, index) => texts.Contains(index)
            ? $"CASE WHEN pg_catalog.octet_length(pg_catalog.convert_to(q.{name},'UTF8')) <= @textlimit THEN q.{name} END" : "q." + name);
        // Enforce the per-field limit inside PostgreSQL, before a potentially large routine body reaches the client.
        var overflow = string.Join(" OR ", textFields.Select(index => $"COALESCE(pg_catalog.octet_length(pg_catalog.convert_to(q.{names[index]},'UTF8')) > @textlimit, false)"));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction; command.CommandTimeout = _timeout;
        command.CommandText = "SELECT " + string.Join(',', selected) + ",(" + overflow + ") FROM (" + sql + ") q(" + string.Join(',', names) + ")";
        void Parameter(string name, DbType type, object value)
        { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.DbType = type; parameter.Value = value; command.Parameters.Add(parameter); }
        for (var index = 0; index < _schemas.Length; index++) { Parameter("schema" + index.ToString(CultureInfo.InvariantCulture), DbType.String, _schemas[index]); }
        Parameter("limit", DbType.Int32, checked(maximum + 1));
        Parameter("textlimit", DbType.Int32, Math.Min(_limits.Relations.MaximumStringBytes, _limits.MaximumMetadataBytes));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (++count > maximum || reader.GetBoolean(fields)) { throw new SchemaCaptureLimitException(); }
            accept(reader);
        }
    }

    private sealed record TypeBuilder(SchemaRelationIdentity Identity, string Kind, string? BaseType, bool IsNullable,
        string? DefaultSql, string? Collation)
    {
        internal List<(int Ordinal, string Label)> Labels { get; } = [];
        internal List<SchemaConstraint> Constraints { get; } = [];
    }
}

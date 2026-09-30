using System.Data;
using System.Data.Common;

namespace BlueTusk.Schema;

// PostgreSQL deparsers and pg_publication_tables can use current catalogue caches
// even when their caller holds an older repeatable-read snapshot. This transient
// attestation must surround materialization; none of its tokens are canonical data.
internal sealed class PostgreSqlCatalogConsistencyAttestation : IDisposable
{
    private const int MaximumTokenCount = 2_000_000;
    private const int TokenAdmissionBytes = 256; // Both retained snapshots and List growth.
    private const int IdentityAdmissionBytes = 1024;
    private static readonly CatalogQuery[] Catalogues =
    [
        new("pg_class", "oid", "0"),
        new("pg_type", "oid", "0"),
        new("pg_enum", "oid", "0"),
        new("pg_constraint", "oid", "0"),
        new("pg_attribute", "attrelid", "attnum"),
        new("pg_index", "indexrelid", "0"),
        new("pg_policy", "oid", "0"),
        new("pg_proc", "oid", "0"),
        new("pg_namespace", "oid", "0"),
        new("pg_publication", "oid", "0"),
        new("pg_publication_rel", "oid", "0"),
        new("pg_publication_namespace", "oid", "0"),
        new("pg_extension", "oid", "0"),
        new("pg_attrdef", "oid", "0"),
        new("pg_collation", "oid", "0"),
        new("pg_language", "oid", "0"),
        new("pg_rewrite", "oid", "0"),
        new("pg_inherits", "inhrelid", "inhseqno"),
        new("pg_operator", "oid", "0"),
        new("pg_opclass", "oid", "0"),
        new("pg_opfamily", "oid", "0"),
        new("pg_am", "oid", "0"),
        new("pg_cast", "oid", "0"),
        new("pg_transform", "oid", "0"),
        new("pg_ts_config", "oid", "0"),
        new("pg_ts_dict", "oid", "0"),
        new("pg_database", "oid", "0"),
        // A narrow SELECT (oid, xmin) grant suffices; no credential fields are read.
        new("pg_authid", "oid", "0"),
    ];

    private readonly DbDataSource _dataSource;
    private readonly int _timeout;
    private readonly long _maximumBytes;
    private readonly CancellationTokenSource _deadline;
    private readonly CatalogSnapshot _held;
    private bool _verified;
    private bool _disposed;

    private PostgreSqlCatalogConsistencyAttestation(DbDataSource dataSource, int timeout, long maximumBytes,
        CancellationTokenSource deadline, CatalogSnapshot held)
    {
        _dataSource = dataSource;
        _timeout = timeout;
        _maximumBytes = maximumBytes;
        _deadline = deadline;
        _held = held;
    }

    // The caller must use this token for every materialization query between
    // BeginAsync and VerifyAsync, so one deadline includes the second pool wait.
    internal CancellationToken CancellationToken => _deadline.Token;

    internal static async ValueTask<PostgreSqlCatalogConsistencyAttestation> BeginAsync(DbDataSource dataSource,
        DbConnection connection, DbTransaction transaction, SchemaCatalogCaptureOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Relations);
        ArgumentNullException.ThrowIfNull(options.Limits);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection) ||
            transaction.IsolationLevel is not (IsolationLevel.RepeatableRead or IsolationLevel.Serializable))
        {
            throw new ArgumentException("Catalogue attestation requires a consistent caller-owned transaction.", nameof(transaction));
        }
        var timeout = options.Relations.CommandTimeoutSeconds;
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, 300);
        var maximumBytes = Math.Min(options.Relations.MaximumMetadataBytes, options.Limits.MaximumMetadataBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, 1024L * 1024 * 1024);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            var held = await ReadSnapshotAsync(connection, transaction, timeout, maximumBytes, deadline.Token).ConfigureAwait(false);
            return new(dataSource, timeout, maximumBytes, deadline, held);
        }
        catch
        {
            deadline.Dispose();
            throw;
        }
    }

    // Called only after every cache-sensitive result has been fully materialized.
    // A new connection/snapshot is mandatory; reusing the held snapshot would not
    // detect committed changes which PostgreSQL's cache lookups already exposed.
    internal async ValueTask VerifyAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_verified) { throw new InvalidOperationException("Catalogue attestation has already completed."); }
        await using var connection = await _dataSource.OpenConnectionAsync(CancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken).ConfigureAwait(false);
        await using (var setup = connection.CreateCommand())
        {
            setup.Transaction = transaction;
            setup.CommandTimeout = _timeout;
            setup.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL search_path = pg_catalog";
            _ = await setup.ExecuteNonQueryAsync(CancellationToken).ConfigureAwait(false);
        }
        var current = await ReadSnapshotAsync(connection, transaction, _timeout, _maximumBytes, CancellationToken).ConfigureAwait(false);
        CancellationToken.ThrowIfCancellationRequested();
        if (_held.Identity != current.Identity || !_held.Tokens.SequenceEqual(current.Tokens))
        {
            throw new SchemaCatalogAttestationMismatchException();
        }
        await transaction.CommitAsync(CancellationToken).ConfigureAwait(false);
        _verified = true;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _deadline.Dispose();
        _disposed = true;
    }

    private static async ValueTask<CatalogSnapshot> ReadSnapshotAsync(DbConnection connection, DbTransaction transaction,
        int timeout, long maximumBytes, CancellationToken cancellationToken)
    {
        var identity = await ReadIdentityAsync(connection, transaction, timeout, cancellationToken).ConfigureAwait(false);
        var bytes = (long)IdentityAdmissionBytes;
        if (bytes > maximumBytes) { throw new SchemaCaptureLimitException(); }
        var tokens = new List<CatalogVersionToken>();
        for (var index = 0; index < Catalogues.Length; index++)
        {
            var catalogue = Catalogues[index];
            var remaining = Math.Min(MaximumTokenCount - tokens.Count, (maximumBytes - bytes) / TokenAdmissionBytes);
            if (remaining < 0) { throw new SchemaCaptureLimitException(); }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = timeout;
            // Identifiers are exclusively static, audited constants above.
            command.CommandText = $"SELECT {catalogue.FirstKey}::bigint, {catalogue.SecondKey}::bigint, xmin::text::bigint FROM pg_catalog.{catalogue.Name} ORDER BY {catalogue.FirstKey}" +
                (catalogue.SecondKey == "0" ? "" : $", {catalogue.SecondKey}") + " LIMIT @limit";
            AddLimit(command, checked((int)remaining + 1));
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                bytes = checked(bytes + TokenAdmissionBytes);
                if (bytes > maximumBytes || tokens.Count >= MaximumTokenCount) { throw new SchemaCaptureLimitException(); }
                tokens.Add(new(index, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }
        return new(identity, tokens);
    }

    private static void AddLimit(DbCommand command, int value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "limit";
        parameter.DbType = DbType.Int32;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }

    private static async ValueTask<ServerIdentity> ReadIdentityAsync(DbConnection connection, DbTransaction transaction,
        int timeout, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = timeout;
        // These built-in values have PostgreSQL-defined fixed bounds. Control
        // system identity and postmaster start also reject routing to another
        // cluster/restarted server when OIDs happen to be identical.
        command.CommandText = """
            SELECT (pg_catalog.pg_control_system()).system_identifier::text,
                (SELECT oid::bigint FROM pg_catalog.pg_database WHERE datname = pg_catalog.current_database()),
                pg_catalog.current_database()::text, COALESCE(pg_catalog.inet_server_addr()::text, ''),
                COALESCE(pg_catalog.inet_server_port(), 0), (EXTRACT(EPOCH FROM pg_catalog.pg_postmaster_start_time()))::text,
                pg_catalog.pg_is_in_recovery(), pg_catalog.current_setting('server_version_num')::int4,
                session_user::text, current_user::text,
                pg_catalog.current_setting('transaction_read_only') = 'on',
                pg_catalog.current_setting('search_path') = 'pg_catalog'
            """;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { throw new InvalidOperationException("PostgreSQL server identity is missing."); }
        var identity = new ServerIdentity(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), reader.GetString(5), reader.GetBoolean(6), reader.GetInt32(7), reader.GetString(8), reader.GetString(9));
        if (!reader.GetBoolean(10) || !reader.GetBoolean(11))
        {
            throw new ArgumentException("Catalogue attestation requires a read-only transaction with an explicit pg_catalog search path.", nameof(transaction));
        }
        if (identity.ServerVersion < 150000) { throw new NotSupportedException("Catalogue attestation requires PostgreSQL 15 or later."); }
        return identity;
    }

    private sealed record CatalogSnapshot(ServerIdentity Identity, List<CatalogVersionToken> Tokens);
    private readonly record struct CatalogQuery(string Name, string FirstKey, string SecondKey);
    private readonly record struct CatalogVersionToken(int Catalogue, long FirstKey, long SecondKey, long Version);
    private sealed record ServerIdentity(string SystemIdentifier, long DatabaseOid, string DatabaseName, string Address,
        int Port, string PostmasterStart, bool IsInRecovery, int ServerVersion, string SessionUser, string CurrentUser);
}

internal sealed class SchemaCatalogAttestationMismatchException : InvalidOperationException
{
    internal SchemaCatalogAttestationMismatchException() : base("Catalogue consistency could not be attested. Concurrent catalogue changes or a different server were observed; retry in a fresh transaction.") { }
}

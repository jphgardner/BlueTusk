using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sql;

public sealed record SqlResultColumn(string Name, string PostgreSqlType, bool IsNullable);

public sealed class SqlContractMismatchException : InvalidOperationException
{
    public SqlContractMismatchException() : base("The PostgreSQL query result does not match its registered typed contract.") { }
}

public sealed class SqlResultLimitException : InvalidOperationException
{
    public SqlResultLimitException() : base("The PostgreSQL query exceeded its registered result bound.") { }
}

/// <summary>Immutable SQL with generated argument binding and result construction. Connections remain caller-owned.</summary>
public sealed class SqlQuery<TArguments, TResult>
{
    private readonly Action<DbCommand, TArguments> _bind;
    private readonly Func<DbDataReader, TResult> _read;

    public SqlQuery(string name, string sql, IEnumerable<SqlResultColumn> columns,
        Action<DbCommand, TArguments> bind, Func<DbDataReader, TResult> read,
        int maximumRows = 1000, int commandTimeoutSeconds = 30,
        int maximumFieldBytes = 1024 * 1024, long maximumResultBytes = 64 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRows, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(commandTimeoutSeconds, 300);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFieldBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumFieldBytes, 64 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResultBytes, maximumFieldBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResultBytes, 1024L * 1024 * 1024);
        if (Encoding.UTF8.GetByteCount(sql) > 1024 * 1024)
        {
            throw new ArgumentException("SQL contracts are bounded to one MiB.", nameof(sql));
        }

        var columnList = new List<SqlResultColumn>();
        foreach (var column in columns)
        {
            if (columnList.Count >= 1024 || column is null || string.IsNullOrWhiteSpace(column.Name) ||
                string.IsNullOrWhiteSpace(column.PostgreSqlType) || Encoding.UTF8.GetByteCount(column.Name) > 63 ||
                column.PostgreSqlType.Length > 512 || column.Name.Contains('\0', StringComparison.Ordinal) ||
                column.PostgreSqlType.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("Result columns exceed their admission contract.", nameof(columns));
            }
            columnList.Add(column);
        }
        Columns = Array.AsReadOnly(columnList.ToArray());
        if (Columns.Count < 1 ||
            Columns.Select(column => column.Name).Distinct(StringComparer.Ordinal).Count() != Columns.Count)
        {
            throw new ArgumentException("Result columns must have unique valid names and PostgreSQL types.", nameof(columns));
        }

        Name = name;
        Sql = sql;
        Fingerprint = ComputeFingerprint(name, sql, Columns, maximumRows, commandTimeoutSeconds, maximumFieldBytes, maximumResultBytes);
        MaximumRows = maximumRows;
        CommandTimeoutSeconds = commandTimeoutSeconds;
        MaximumFieldBytes = maximumFieldBytes;
        MaximumResultBytes = maximumResultBytes;
        _bind = bind;
        _read = read;
    }

    public string Name { get; }
    public string Sql { get; }
    public string Fingerprint { get; }
    public ReadOnlyCollection<SqlResultColumn> Columns { get; }
    public int MaximumRows { get; }
    public int CommandTimeoutSeconds { get; }
    public int MaximumFieldBytes { get; }
    public long MaximumResultBytes { get; }

    public async IAsyncEnumerable<TResult> ReadAsync(DbConnection connection, TArguments arguments,
        DbTransaction? transaction = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = CreateCommand(connection, transaction, arguments);
        // The provider may buffer a parameterless query before returning its reader.
        // Bound the server result as well as the rows delivered to the caller.
        command.CommandText = "SELECT * FROM (\n" + PostgreSqlStatementGuard.AdmitReadQuery(Sql) +
            "\n) AS bluetusk_bounded LIMIT " + (MaximumRows + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (command is not BlueTuskCommand blueTuskCommand)
        {
            throw new NotSupportedException("Typed SQL reads require a BlueTusk command with a streaming reader.");
        }
        blueTuskCommand.ExecutionMode = BlueTuskCommandExecutionMode.Extended;
        if (!blueTuskCommand.WillStreamReader(CommandBehavior.Default))
        {
            throw new NotSupportedException("Typed SQL reads require a streaming reader; buffered reader mode is unsupported.");
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        ValidateResultShape(reader, connection);
        var count = 0;
        long encodedBytes = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (++count > MaximumRows)
            {
                throw new SqlResultLimitException();
            }

            for (var ordinal = 0; ordinal < Columns.Count; ordinal++)
            {
                if (reader is not BlueTuskDataReader boundedReader)
                {
                    throw new NotSupportedException("Typed SQL byte admission requires BlueTuskDataReader.");
                }
                var fieldBytes = boundedReader.GetFieldByteLength(ordinal);
                if (!Columns[ordinal].IsNullable && fieldBytes is null)
                {
                    throw new SqlContractMismatchException();
                }
                encodedBytes += (fieldBytes ?? 0) + 4L;
                if (fieldBytes > MaximumFieldBytes || encodedBytes > MaximumResultBytes) { throw new SqlResultLimitException(); }
            }

            yield return _read(reader);
        }

        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new SqlContractMismatchException();
        }
    }

    /// <summary>Asks PostgreSQL to describe a query through a zero-row read-only wrapper; it does not prove application authorization.</summary>
    public async ValueTask ValidateAsync(DbDataSource dataSource, TArguments sampleArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await using (var setup = connection.CreateCommand())
        {
            setup.Transaction = transaction;
            setup.CommandTimeout = CommandTimeoutSeconds;
            setup.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL standard_conforming_strings = on";
            _ = await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = CreateCommand(connection, transaction, sampleArguments))
        {
            // SQL is trusted application source. Only trailing statement terminators
            // are removed; PostgreSQL validates grammar, types and read-only behavior.
            command.CommandText = "SELECT * FROM (\n" + PostgreSqlStatementGuard.AdmitReadQuery(Sql) + "\n) AS bluetusk_contract LIMIT 0";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            ValidateResultShape(reader, connection);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, TArguments arguments)
    {
        if (connection.State != ConnectionState.Open || transaction is not null && !ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException("Typed queries require an open connection and its own active transaction.", nameof(connection));
        }

        var command = connection.CreateCommand();
        try
        {
            command.Transaction = transaction;
            command.CommandText = Sql;
            command.CommandTimeout = CommandTimeoutSeconds;
            _bind(command, arguments);
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private void ValidateResultShape(DbDataReader reader, DbConnection connection)
    {
        if (reader.FieldCount != Columns.Count)
        {
            throw new SqlContractMismatchException();
        }

        for (var ordinal = 0; ordinal < Columns.Count; ordinal++)
        {
            var column = Columns[ordinal];
            if (!string.Equals(reader.GetName(ordinal), column.Name, StringComparison.Ordinal))
            {
                throw new SqlContractMismatchException();
            }

            if (SqlCatalogueTypeSpec.TryParse(column.PostgreSqlType, out var declared))
            {
                if (connection is not BlueTuskConnection blueTuskConnection || reader is not BlueTuskDataReader blueTuskReader ||
                    !declared.TryResolve(blueTuskConnection.TypeRegistry, out var expectedOid))
                {
                    throw new SqlContractMismatchException();
                }
                if (blueTuskReader.GetPostgreSqlTypeOid(ordinal) != expectedOid)
                {
                    throw new SqlContractMismatchException();
                }
            }
            else if (!string.Equals(reader.GetDataTypeName(ordinal), column.PostgreSqlType, StringComparison.Ordinal))
            {
                throw new SqlContractMismatchException();
            }
        }
    }

    private static string ComputeFingerprint(string name, string sql, IEnumerable<SqlResultColumn> columns, int maximumRows, int timeout,
        int maximumFieldBytes, long maximumResultBytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, "BlueTusk.Sql:1");
        Add(hash, name);
        Add(hash, sql.Replace("\r\n", "\n", StringComparison.Ordinal));
        Add(hash, maximumRows.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(hash, timeout.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(hash, maximumFieldBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(hash, maximumResultBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var column in columns)
        {
            Add(hash, column.Name);
            Add(hash, column.PostgreSqlType);
            Add(hash, column.IsNullable ? "nullable" : "required");
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Add(IncrementalHash hash, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

internal readonly record struct SqlCatalogueTypeSpec(string QualifiedName, BlueTuskTypeKind Kind,
    uint BaseOid, bool IsArray)
{
    internal static bool TryParse(string annotation, out SqlCatalogueTypeSpec value)
    {
        var isArray = annotation.EndsWith("[]", StringComparison.Ordinal);
        var scalar = isArray ? annotation[..^2] : annotation;
        if (scalar.StartsWith("enum:", StringComparison.Ordinal))
        {
            value = new(scalar[5..], BlueTuskTypeKind.Enum, 0, isArray);
            return true;
        }
        if (scalar.StartsWith("domain:", StringComparison.Ordinal))
        {
            var separator = scalar.LastIndexOf(':');
            if (separator > 7 && BaseOidFor(scalar[(separator + 1)..]) is { } baseOid)
            {
                value = new(scalar[7..separator], BlueTuskTypeKind.Domain, baseOid, isArray);
                return true;
            }
        }
        value = default;
        return false;
    }

    internal bool TryResolve(BlueTuskTypeRegistry registry, out uint oid)
    {
        oid = 0;
        BlueTuskTypeName name;
        try { name = BlueTuskTypeName.Parse(QualifiedName); }
        catch (ArgumentException) { return false; }
        if (!registry.TryGetType(name, out var type, out var codec) || type is null || codec is null ||
            type.Kind != Kind || Kind == BlueTuskTypeKind.Domain && type.BaseType?.Oid != BaseOid)
        {
            return false;
        }
        var id = IsArray ? type.ArrayType : type.Id;
        if (id is null || !registry.TryGetCodec(id.Value, out var resolvedCodec) || resolvedCodec is null)
        {
            return false;
        }
        // PostgreSQL describes a scalar domain expression with its base OID.
        // The catalogue still validates the declared domain identity and base codec;
        // an array of that domain retains its distinct array OID.
        oid = Kind == BlueTuskTypeKind.Domain && !IsArray ? BaseOid : id.Value.Oid;
        return true;
    }

    private static uint? BaseOidFor(string name) => name switch
    {
        "bool" => 16,
        "int2" => 21,
        "int4" => 23,
        "int8" => 20,
        "text" => 25,
        "varchar" => 1043,
        "uuid" => 2950,
        "float4" => 700,
        "float8" => 701,
        "numeric" or "numeric-precise" => 1700,
        "timestamp" => 1114,
        "timestamptz" => 1184,
        "bytea" => 17,
        "json" => 114,
        "jsonb" => 3802,
        "date" => 1082,
        "time" => 1083,
        "interval-pg" => 1186,
        _ => null,
    };
}

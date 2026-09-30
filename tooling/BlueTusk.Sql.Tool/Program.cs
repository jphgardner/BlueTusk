using System.Data;
using System.Data.Common;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Schema;
using BlueTusk.Sql;
using BlueTusk.Sql.SourceGeneration;
using BlueTusk.TypeSystem;
using static BlueTusk.Sql.SourceGeneration.SqlContractModel;

return await SqlCli.RunAsync(args, Console.Out, Console.Error);

internal static class SqlCli
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<int> RunAsync(string[] arguments, TextWriter output, TextWriter error,
        DbDataSource? injectedDataSource = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (arguments.Length == 0 || arguments[0] is "help" or "--help")
            {
                await output.WriteLineAsync("bluetusk-sql validate --schema app.bluetusk-schema.json|app.bluetusk-catalog.json --schemas app --output app.bluetusk-sql.xml --query orders.sql [--query other.sql]\n" +
                    "Reads BLUETUSK_SQL_CONNECTION_STRING. Exit 2 = schema drift; 1 = invalid query/operation. Add snapshot and validation files as compiler AdditionalFiles.").ConfigureAwait(false);
                return 0;
            }
            if (arguments[0] != "validate" || arguments.Length > 2009 || arguments.Any(argument => argument.Length > 4096))
            { throw new ArgumentException("Invalid command or bounds."); }
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var paths = new List<string>();
            for (var index = 1; index < arguments.Length; index += 2)
            {
                if (index + 1 == arguments.Length) { throw new ArgumentException("Missing option value."); }
                var key = arguments[index];
                if (key == "--query") { if (paths.Count >= 1000) { throw new ArgumentException("Query bound exceeded."); } paths.Add(arguments[index + 1]); }
                else if (key is not ("--schema" or "--schemas" or "--output") || !options.TryAdd(key, arguments[index + 1]))
                { throw new ArgumentException("Unknown or duplicate option."); }
            }
            if (paths.Count == 0 || options.Count != 3 || options.Values.Any(string.IsNullOrWhiteSpace) ||
                !(options["--schema"].EndsWith(".bluetusk-schema.json", StringComparison.OrdinalIgnoreCase) ||
                    options["--schema"].EndsWith(".bluetusk-catalog.json", StringComparison.OrdinalIgnoreCase)) ||
                !options["--output"].EndsWith(".bluetusk-sql.xml", StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException("A schema snapshot, output and queries are required."); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var token = deadline.Token;
            var snapshotBytes = await ReadAsync(options["--schema"], SchemaSnapshotSerializer.MaximumDocumentBytes, token).ConfigureAwait(false);
            var snapshotText = StrictUtf8.GetString(snapshotBytes);
            var catalogue = options["--schema"].EndsWith(".bluetusk-catalog.json", StringComparison.OrdinalIgnoreCase);
            var catalogueSnapshot = catalogue ? SchemaCatalogSerializer.Deserialize(snapshotBytes) : null;
            var baselineFingerprint = catalogueSnapshot is not null ? catalogueSnapshot.Fingerprint :
                SchemaSnapshotSerializer.Deserialize(snapshotBytes).Fingerprint;
            var queries = new List<Query>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            long aggregateSqlBytes = 0;
            foreach (var path in paths)
            {
                if (!path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("SQL files are required."); }
                var bytes = await ReadAsync(path, 1024 * 1024, token).ConfigureAwait(false);
                aggregateSqlBytes += bytes.Length;
                if (aggregateSqlBytes > 16 * 1024 * 1024) { throw new ArgumentException("Aggregate SQL bound exceeded."); }
                var query = Parse(new SqlSource(path, StrictUtf8.GetString(bytes)), out _)
                    ?? throw new ArgumentException("Invalid annotated SQL contract.");
                if (query.UsesCatalogueTypes && (catalogueSnapshot is null || !query.RequiresValidation))
                { throw new ArgumentException("Domain and enum bindings require an attested catalogue receipt."); }
                _ = PostgreSqlStatementGuard.AdmitReadQuery(query.Sql);
                if (!identities.Add(query.Identity)) { throw new ArgumentException("Duplicate query identity."); }
                queries.Add(query);
            }
            DbDataSource? ownedDataSource = null;
            try
            {
                var dataSource = injectedDataSource;
                if (dataSource is null)
                {
                    var connection = Environment.GetEnvironmentVariable("BLUETUSK_SQL_CONNECTION_STRING");
                    if (string.IsNullOrWhiteSpace(connection)) { throw new ArgumentException("A database connection is required."); }
                    dataSource = ownedDataSource = BlueTuskDataSource.Create(connection);
                }
                await using var connectionScope = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var transaction = await connectionScope.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
                await using (var setup = connectionScope.CreateCommand())
                {
                    setup.Transaction = transaction;
                    setup.CommandTimeout = 30;
                    setup.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL standard_conforming_strings = on; SET LOCAL search_path = pg_catalog; SET LOCAL statement_timeout = '30s'";
                    _ = await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                var captureOptions = new SchemaCaptureOptions { Schemas = options["--schemas"].Split(',') };
                var capture = catalogue ? new PostgreSqlSchemaCatalogCapture(dataSource, new() { Relations = captureOptions }) : null;
                var currentFingerprint = capture is not null ?
                    (await capture.CaptureInTransactionAsync(connectionScope, transaction, token).ConfigureAwait(false)).Fingerprint :
                    (await new PostgreSqlSchemaCapture(dataSource, captureOptions).CaptureInTransactionAsync(connectionScope, transaction, token).ConfigureAwait(false)).Fingerprint;
                if (currentFingerprint != baselineFingerprint)
                {
                    await error.WriteLineAsync("SQL validation rejected schema drift.").ConfigureAwait(false);
                    return 2;
                }
                foreach (var query in queries)
                {
                    if (query.UsesCatalogueTypes)
                    {
                        if (connectionScope is not BlueTuskConnection blueTuskConnection || catalogueSnapshot is null)
                        { throw new ArgumentException("Domain and enum validation requires a BlueTusk catalogue connection."); }
                        ValidateCatalogueTypes(query, catalogueSnapshot, blueTuskConnection.TypeRegistry);
                    }
                    await using var command = connectionScope.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandTimeout = 30;
                    command.CommandText = "SELECT * FROM (\n" + PostgreSqlStatementGuard.AdmitReadQuery(query.Sql) + "\n) AS bluetusk_contract LIMIT 0";
                    foreach (var parameter in query.Parameters)
                    {
                        command.Parameters.Add(parameter.Type.QualifiedName is null
                            ? new BlueTuskParameter { PostgreSqlTypeOid = parameter.Type.Oid, Value = DBNull.Value }
                            : new BlueTuskParameter { PostgreSqlTypeName = parameter.Type.ParameterTypeName, Value = DBNull.Value });
                    }
                    await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    if (reader.FieldCount != query.Columns.Count) { throw new SqlContractMismatchException(); }
                    var typedReader = reader as BlueTuskDataReader;
                    if (query.Columns.Any(column => column.Type.QualifiedName is not null) && typedReader is null)
                    { throw new SqlContractMismatchException(); }
                    for (var ordinal = 0; ordinal < query.Columns.Count; ordinal++)
                    {
                        var column = query.Columns[ordinal];
                        if (reader.GetName(ordinal) != column.Name ||
                            (column.Type.QualifiedName is null && reader.GetDataTypeName(ordinal) != column.Type.PostgresName) ||
                            (column.Type.QualifiedName is not null &&
                                typedReader!.GetPostgreSqlTypeOid(ordinal) != ResolveCatalogueOid(column.Type, ((BlueTuskConnection)connectionScope).TypeRegistry)))
                        { throw new SqlContractMismatchException(); }
                    }
                }
                // A second capture in the same held snapshot attests the complete
                // description interval against a fresh catalogue observation.
                if (capture is not null &&
                    (await capture.CaptureInTransactionAsync(connectionScope, transaction, token).ConfigureAwait(false)).Fingerprint != currentFingerprint)
                { throw new SchemaConcurrentDdlException(); }
                await transaction.CommitAsync(token).ConfigureAwait(false);
                var receipt = SqlValidationDocument.Write(currentFingerprint, DocumentFingerprint(snapshotText), queries);
                await WriteAsync(options["--output"], StrictUtf8.GetBytes(receipt), token).ConfigureAwait(false);
                await output.WriteLineAsync($"Validated {queries.Count} queries against schema {currentFingerprint}.").ConfigureAwait(false);
                return 0;
            }
            finally { if (ownedDataSource is not null) { await ownedDataSource.DisposeAsync().ConfigureAwait(false); } }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var category = exception switch
            {
                OperationCanceledException => "cancelled or timed out",
                SqlContractMismatchException => "query result contract mismatch",
                IOException => "artifact file operation failed",
                _ => "invalid contract, schema or database operation",
            };
            await error.WriteLineAsync("SQL validation failed: " + category + ".").ConfigureAwait(false);
            return 1;
        }
    }

    private static void ValidateCatalogueTypes(Query query, SchemaCatalogSnapshot snapshot, BlueTuskTypeRegistry registry)
    {
        foreach (var member in query.Parameters.Concat(query.Columns))
        {
            if (member.Type.QualifiedName is not { } name) { continue; }
            var separator = name.IndexOf('.');
            if (!snapshot.Types.Any(type => type.Identity.Schema == name[..separator] &&
                    type.Identity.Name == name[(separator + 1)..] && type.Kind.Length == 1 &&
                    type.Kind[0] == member.Type.CatalogKind))
            { throw new ArgumentException("A declared catalogue type is absent from the attested snapshot."); }
            _ = ResolveCatalogueOid(member.Type, registry);
        }
    }

    private static uint ResolveCatalogueOid(PgType declared, BlueTuskTypeRegistry registry)
    {
        var name = BlueTuskTypeName.Parse(declared.QualifiedName!);
        if (!registry.TryGetType(name, out var type, out var codec) || type is null || codec is null ||
            (declared.CatalogKind == 'e' ? type.Kind != BlueTuskTypeKind.Enum : type.Kind != BlueTuskTypeKind.Domain) ||
            declared.CatalogKind == 'd' && type.BaseType?.Oid != declared.BaseOid)
        { throw new ArgumentException("A declared catalogue type changed kind or base type."); }
        var id = declared.IsArray ? type.ArrayType : type.Id;
        if (id is null || !registry.TryGetCodec(id.Value, out var arrayCodec) || arrayCodec is null)
        { throw new ArgumentException("A declared catalogue type lacks a supported codec."); }
        // RowDescription exposes a scalar domain through its base type OID.
        return declared.CatalogKind == 'd' && !declared.IsArray ? declared.BaseOid : id.Value.Oid;
    }

    private static async Task<byte[]> ReadAsync(string path, int maximumBytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) { throw new ArgumentException("Input byte bound exceeded."); }
        using var bytes = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > maximumBytes) { throw new ArgumentException("Input byte bound exceeded."); }
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    private static async Task WriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination) ?? throw new ArgumentException("An output directory is required.");
        var temporary = Path.Combine(directory, ".bluetusk-sql-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                created = true;
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (created && File.Exists(temporary)) { File.Delete(temporary); } }
    }
}

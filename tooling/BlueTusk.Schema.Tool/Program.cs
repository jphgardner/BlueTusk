using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Data;
using BlueTusk.Schema;

return await SchemaCli.RunAsync(args, Console.Out, Console.Error);

internal static class SchemaCli
{
    internal static async Task<int> RunAsync(string[] arguments, TextWriter output, TextWriter error,
        DbDataSource? injectedDataSource = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (arguments.Length == 0 || arguments[0] is "help" or "--help")
            {
                await output.WriteLineAsync("bluetusk-schema capture --schemas app,public --output schema.json\n" +
                    "bluetusk-schema compare --before before.json --after after.json [--output changes.json]\n" +
                    "bluetusk-schema check --baseline schema.json --schemas app,public [--output changes.json]\n" +
                    "bluetusk-schema capture-catalog --schemas app,public --output catalog.json\n" +
                    "bluetusk-schema compare-catalog --before before.json --after after.json [--output changes.json]\n" +
                    "bluetusk-schema check-catalog --baseline catalog.json --schemas app,public [--output changes.json]\n" +
                    "Database commands read BLUETUSK_SCHEMA_CONNECTION_STRING. Exit 2 = incompatibility/drift; 3 = review; 1 = invalid operation.").ConfigureAwait(false);
                return 0;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var token = deadline.Token;
            var options = Parse(arguments);
            if (arguments[0] == "compare-catalog")
            {
                RequireOnly(options, "before", "after", "output");
                var before = SchemaCatalogSerializer.Deserialize(await ReadBytesAsync(Required(options, "before"), token).ConfigureAwait(false));
                var after = SchemaCatalogSerializer.Deserialize(await ReadBytesAsync(Required(options, "after"), token).ConfigureAwait(false));
                return await ReportCatalogAsync(before, after, options.GetValueOrDefault("output"), false, output, token).ConfigureAwait(false);
            }
            if (arguments[0] == "compare")
            {
                RequireOnly(options, "before", "after", "output");
                var before = await ReadAsync(Required(options, "before"), token).ConfigureAwait(false);
                var after = await ReadAsync(Required(options, "after"), token).ConfigureAwait(false);
                return await ReportAsync(before, after, options.GetValueOrDefault("output"), false, output, token).ConfigureAwait(false);
            }
            if (arguments[0] is not ("capture" or "check" or "capture-catalog" or "check-catalog")) { throw new ArgumentException("Unknown schema command."); }
            RequireOnly(options, arguments[0] is "capture" or "capture-catalog" ? ["schemas", "output"] : ["schemas", "baseline", "output"]);
            var schemas = Required(options, "schemas").Split(',', StringSplitOptions.None);
            DbDataSource? ownedDataSource = null;
            try
            {
                var dataSource = injectedDataSource;
                if (dataSource is null)
                {
                    var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_SCHEMA_CONNECTION_STRING");
                    if (string.IsNullOrWhiteSpace(connectionString)) { throw new ArgumentException("BLUETUSK_SCHEMA_CONNECTION_STRING is required."); }
                    dataSource = ownedDataSource = BlueTuskDataSource.Create(connectionString);
                }
                if (arguments[0].EndsWith("-catalog", StringComparison.Ordinal))
                {
                    var catalog = await new PostgreSqlSchemaCatalogCapture(dataSource, new() { Relations = new() { Schemas = schemas } }).CaptureAsync(token).ConfigureAwait(false);
                    if (arguments[0] == "capture-catalog")
                    {
                        await WriteAtomicAsync(Required(options, "output"), SchemaCatalogSerializer.Serialize(catalog), token).ConfigureAwait(false);
                        await output.WriteLineAsync($"Captured catalogue; fingerprint {catalog.Fingerprint}.").ConfigureAwait(false);
                        return 0;
                    }
                    var baselineCatalog = SchemaCatalogSerializer.Deserialize(await ReadBytesAsync(Required(options, "baseline"), token).ConfigureAwait(false));
                    return await ReportCatalogAsync(baselineCatalog, catalog, options.GetValueOrDefault("output"), true, output, token).ConfigureAwait(false);
                }
                var current = await new PostgreSqlSchemaCapture(dataSource, new() { Schemas = schemas }).CaptureAsync(token).ConfigureAwait(false);
                if (arguments[0] == "capture")
                {
                    await WriteAtomicAsync(Required(options, "output"), SchemaSnapshotSerializer.Serialize(current), token).ConfigureAwait(false);
                    await output.WriteLineAsync($"Captured {current.Relations.Count} relations; fingerprint {current.Fingerprint}.").ConfigureAwait(false);
                    return 0;
                }
                var baseline = await ReadAsync(Required(options, "baseline"), token).ConfigureAwait(false);
                return await ReportAsync(baseline, current, options.GetValueOrDefault("output"), true, output, token).ConfigureAwait(false);
            }
            finally { if (ownedDataSource is not null) { await ownedDataSource.DisposeAsync().ConfigureAwait(false); } }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Database exceptions can carry SQL/default/policy text or connection data.
            // A stable category is enough for automation; never print the exception body.
            var category = exception switch
            {
                SchemaCaptureLimitException => "metadata or document limit exceeded",
                SchemaConcurrentDdlException => "catalogue or verification source changed during capture",
                JsonException => "invalid snapshot contract or fingerprint",
                ArgumentException => "invalid command or admission contract",
                OperationCanceledException => "cancelled or timed out",
                IOException => "snapshot file operation failed",
                _ => "database or schema operation failed",
            };
            await error.WriteLineAsync("Schema verification failed: " + category + ".").ConfigureAwait(false);
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] arguments)
    {
        if (arguments.Length > 17 || arguments.Any(argument => argument.Length > 4096)) { throw new ArgumentException("Command bounds exceeded."); }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Length; index += 2)
        {
            if (index + 1 == arguments.Length || !arguments[index].StartsWith("--", StringComparison.Ordinal) ||
                !options.TryAdd(arguments[index][2..], arguments[index + 1])) { throw new ArgumentException("Invalid or duplicate command option."); }
        }
        return options;
    }

    private static void RequireOnly(Dictionary<string, string> options, params string[] allowed)
    {
        if (options.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal))) { throw new ArgumentException("Unknown command option."); }
    }

    private static string Required(Dictionary<string, string> options, string name) => options.TryGetValue(name, out var value) && value.Length > 0
        ? value : throw new ArgumentException("Required command option is missing.");

    private static async Task<SchemaSnapshot> ReadAsync(string path, CancellationToken cancellationToken) =>
        SchemaSnapshotSerializer.Deserialize(await ReadBytesAsync(path, cancellationToken).ConfigureAwait(false));

    private static async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > SchemaSnapshotSerializer.MaximumDocumentBytes) { throw new SchemaCaptureLimitException(); }
        using var result = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > SchemaSnapshotSerializer.MaximumDocumentBytes) { throw new SchemaCaptureLimitException(); }
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination) ?? throw new ArgumentException("An output directory is required.");
        var temporary = Path.Combine(directory, ".bluetusk-schema-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                created = true;
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (created && File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private static async Task<int> ReportAsync(SchemaSnapshot before, SchemaSnapshot after, string? reportPath, bool strict,
        TextWriter output, CancellationToken cancellationToken)
    {
        var comparison = SchemaCompatibility.Compare(before, after);
        var report = new SchemaCliReport(1, before.Fingerprint, after.Fingerprint, before.Fingerprint != after.Fingerprint,
            comparison.HasIncompatibleChanges, comparison.RequiresReview, comparison.Changes);
        if (reportPath is not null)
        {
            await WriteAtomicAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, SchemaCliJson.Default.SchemaCliReport), cancellationToken).ConfigureAwait(false);
        }
        await output.WriteLineAsync($"Schema changes: {comparison.Changes.Count}; drift={report.HasDrift}; incompatible={report.HasIncompatibleChanges}; review={report.RequiresReview}.").ConfigureAwait(false);
        return strict && report.HasDrift || report.HasIncompatibleChanges ? 2 : report.RequiresReview ? 3 : 0;
    }

    private static async Task<int> ReportCatalogAsync(SchemaCatalogSnapshot before, SchemaCatalogSnapshot after, string? reportPath,
        bool strict, TextWriter output, CancellationToken cancellationToken)
    {
        var comparison = SchemaCatalogCompatibility.Compare(before, after);
        var report = new SchemaCatalogCliReport(1, before.Fingerprint, after.Fingerprint, before.Fingerprint != after.Fingerprint,
            comparison.HasIncompatibleChanges, comparison.RequiresReview, comparison.Relations.Changes, comparison.Changes);
        if (reportPath is not null)
        { await WriteAtomicAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, SchemaCliJson.Default.SchemaCatalogCliReport), cancellationToken).ConfigureAwait(false); }
        await output.WriteLineAsync($"Catalogue changes: {comparison.Relations.Changes.Count + comparison.Changes.Count}; drift={report.HasDrift}; incompatible={report.HasIncompatibleChanges}; review={report.RequiresReview}.").ConfigureAwait(false);
        return strict && report.HasDrift || report.HasIncompatibleChanges ? 2 : report.RequiresReview ? 3 : 0;
    }
}

internal sealed record SchemaCliReport(int FormatVersion, string BeforeFingerprint, string AfterFingerprint,
    bool HasDrift, bool HasIncompatibleChanges, bool RequiresReview, IReadOnlyList<SchemaChange> Changes);
internal sealed record SchemaCatalogCliReport(int FormatVersion, string BeforeFingerprint, string AfterFingerprint,
    bool HasDrift, bool HasIncompatibleChanges, bool RequiresReview, IReadOnlyList<SchemaChange> RelationChanges, IReadOnlyList<SchemaCatalogChange> Changes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SchemaCliReport))]
[JsonSerializable(typeof(SchemaCatalogCliReport))]
internal sealed partial class SchemaCliJson : JsonSerializerContext;

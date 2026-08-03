using System.Buffers;
using System.Text.Json;
using BlueTusk.Streams;

namespace BlueTusk.Sync;

/// <summary>Transforms already-mapped mutations as one versioned pipeline stage.</summary>
public interface ISyncTransformStage
{
    SyncTransformVersion Version { get; }

    ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
        ChangeTransaction transaction,
        IReadOnlyList<SyncMutation> mutations,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
        ChangeSnapshotBatch batch,
        IReadOnlyList<SyncSnapshotMutation> mutations,
        CancellationToken cancellationToken = default);
}

/// <summary>Composes a source mapper with ordered, fingerprinted mutation stages.</summary>
public sealed class CompositeSyncTransform : ISyncTransform
{
    private readonly ISyncTransform _source;
    private readonly ISyncTransformStage[] _stages;

    public CompositeSyncTransform(
        string name,
        string version,
        ISyncTransform source,
        IEnumerable<ISyncTransformStage> stages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stages);
        _source = source;
        _stages = stages.ToArray();
        if (_stages.Any(static stage => stage is null))
        {
            throw new ArgumentException("Transform stages cannot contain null.", nameof(stages));
        }

        Version = CreateVersion(name, version, source.Version, _stages);
    }

    public SyncTransformVersion Version { get; }

    public async ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
        ChangeTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var mutations = await _source.TransformTransactionAsync(transaction, cancellationToken)
            .ConfigureAwait(false) ??
            throw new SyncPoisonRecordException("The source transform returned null transaction mutations.");
        var sourceIds = mutations.Select(static mutation => mutation.ChangeId).ToHashSet();
        foreach (var stage in _stages)
        {
            mutations = await stage.TransformTransactionAsync(
                transaction,
                mutations,
                cancellationToken).ConfigureAwait(false) ??
                throw new SyncPoisonRecordException(
                    $"Transform stage '{stage.Version.Name}' returned null transaction mutations.");
        }

        if (mutations.Any(mutation =>
                mutation.ChangeId.Source != transaction.Source ||
                mutation.ChangeId.CommitEndPosition != transaction.CommitEndPosition ||
                mutation.ChangeId.TransactionId != transaction.TransactionId ||
                !sourceIds.Contains(mutation.ChangeId)))
        {
            throw new SyncPoisonRecordException(
                "A transform stage changed the stable source transaction identity.");
        }

        return mutations;
    }

    public async ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
        ChangeSnapshotBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var mutations = await _source.TransformSnapshotBatchAsync(batch, cancellationToken)
            .ConfigureAwait(false) ??
            throw new SyncPoisonRecordException("The source transform returned null snapshot mutations.");
        var sourceIds = mutations.Select(static mutation => mutation.RowId).ToHashSet();
        foreach (var stage in _stages)
        {
            mutations = await stage.TransformSnapshotBatchAsync(
                batch,
                mutations,
                cancellationToken).ConfigureAwait(false) ??
                throw new SyncPoisonRecordException(
                    $"Transform stage '{stage.Version.Name}' returned null snapshot mutations.");
        }

        if (mutations.Any(mutation =>
                mutation.RowId.Epoch != batch.Epoch.Value ||
                !sourceIds.Contains(mutation.RowId)))
        {
            throw new SyncPoisonRecordException(
                "A transform stage changed the stable source snapshot epoch.");
        }

        return mutations;
    }

    private static SyncTransformVersion CreateVersion(
        string name,
        string version,
        SyncTransformVersion source,
        IReadOnlyList<ISyncTransformStage> stages)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", 1);
            writer.WriteString("sourceName", source.Name);
            writer.WriteString("sourceFingerprint", source.Fingerprint);
            writer.WriteStartArray("stages");
            foreach (var stage in stages)
            {
                writer.WriteStartObject();
                writer.WriteString("name", stage.Version.Name);
                writer.WriteString("fingerprint", stage.Version.Fingerprint);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return SyncTransformVersion.Create(name, version, buffer.WrittenSpan);
    }
}

/// <summary>Filters mapped mutations with application predicates carrying an explicit version.</summary>
public sealed class SyncPredicateTransformStage : ISyncTransformStage
{
    private readonly Func<SyncMutation, bool> _transactionPredicate;
    private readonly Func<SyncSnapshotMutation, bool> _snapshotPredicate;

    public SyncPredicateTransformStage(
        SyncTransformVersion version,
        Func<SyncMutation, bool> transactionPredicate,
        Func<SyncSnapshotMutation, bool> snapshotPredicate)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(transactionPredicate);
        ArgumentNullException.ThrowIfNull(snapshotPredicate);
        Version = version;
        _transactionPredicate = transactionPredicate;
        _snapshotPredicate = snapshotPredicate;
    }

    public SyncTransformVersion Version { get; }

    public ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
        ChangeTransaction transaction,
        IReadOnlyList<SyncMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(mutations);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<SyncMutation>>(
            mutations.Where(_transactionPredicate).ToArray());
    }

    public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
        ChangeSnapshotBatch batch,
        IReadOnlyList<SyncSnapshotMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(mutations);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<SyncSnapshotMutation>>(
            mutations.Where(_snapshotPredicate).ToArray());
    }
}

/// <summary>Configures deterministic JSON redaction, enrichment, flattening, and tenant routing.</summary>
public sealed record JsonSyncTransformStageOptions
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    public IReadOnlyList<string> RedactedPaths { get; init; } = [];

    public IReadOnlyDictionary<string, string> EnrichmentJson { get; init; } =
        new Dictionary<string, string>();

    public bool FlattenObjects { get; init; }

    public string FlattenSeparator { get; init; } = ".";

    public string? TenantPropertyPath { get; init; }

    public bool RequireTenant { get; init; } = true;

    public int MaximumDocumentBytes { get; init; } = 1024 * 1024;
}

/// <summary>Applies bounded declarative JSON materialisation policies.</summary>
public sealed class JsonSyncTransformStage : ISyncTransformStage
{
    private const int MaximumConfigurationEntries = 1024;

    private readonly HashSet<string> _redactedPaths;
    private readonly KeyValuePair<string, JsonElement>[] _enrichment;
    private readonly string[]? _tenantPath;
    private readonly bool _flatten;
    private readonly string _separator;
    private readonly bool _requireTenant;
    private readonly int _maximumDocumentBytes;

    public JsonSyncTransformStage(JsonSyncTransformStageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Version);
        ArgumentNullException.ThrowIfNull(options.RedactedPaths);
        ArgumentNullException.ThrowIfNull(options.EnrichmentJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FlattenSeparator);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumDocumentBytes, 1);
        if (options.FlattenSeparator.Length > 8)
        {
            throw new ArgumentException(
                "The flatten separator cannot exceed eight characters.",
                nameof(options));
        }

        if (options.RedactedPaths.Count > MaximumConfigurationEntries ||
            options.EnrichmentJson.Count > MaximumConfigurationEntries)
        {
            throw new ArgumentException(
                $"JSON transform configuration cannot exceed {MaximumConfigurationEntries} redaction or enrichment entries.",
                nameof(options));
        }

        _redactedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in options.RedactedPaths)
        {
            ValidatePath(path, nameof(options));
            if (!_redactedPaths.Add(path))
            {
                throw new ArgumentException($"Redacted path '{path}' is duplicated.", nameof(options));
            }
        }

        var enrichment = new List<KeyValuePair<string, JsonElement>>(options.EnrichmentJson.Count);
        foreach (var entry in options.EnrichmentJson.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            ValidateProperty(entry.Key, nameof(options));
            if (options.FlattenObjects &&
                entry.Key.Contains(options.FlattenSeparator, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Enrichment property '{entry.Key}' contains the configured flatten separator.",
                    nameof(options));
            }

            if (_redactedPaths.Contains(entry.Key))
            {
                throw new ArgumentException(
                    $"Enrichment property '{entry.Key}' is also redacted.",
                    nameof(options));
            }

            try
            {
                using var document = JsonDocument.Parse(entry.Value);
                enrichment.Add(new KeyValuePair<string, JsonElement>(entry.Key, document.RootElement.Clone()));
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(
                    $"Enrichment property '{entry.Key}' does not contain valid JSON.",
                    nameof(options),
                    exception);
            }
        }

        if (enrichment.Sum(static entry => entry.Value.GetRawText().Length) >
            options.MaximumDocumentBytes)
        {
            throw new ArgumentException(
                "The enrichment configuration exceeds the maximum transformed document size.",
                nameof(options));
        }

        if (options.TenantPropertyPath is { } tenantPath)
        {
            ValidatePath(tenantPath, nameof(options));
            _tenantPath = tenantPath.Split('.');
        }

        _enrichment = enrichment.ToArray();
        _flatten = options.FlattenObjects;
        _separator = options.FlattenSeparator;
        _requireTenant = options.RequireTenant;
        _maximumDocumentBytes = options.MaximumDocumentBytes;
        Version = CreateVersion(options, _redactedPaths, _enrichment);
    }

    public SyncTransformVersion Version { get; }

    public ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
        ChangeTransaction transaction,
        IReadOnlyList<SyncMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(mutations);
        var transformed = new SyncMutation[mutations.Count];
        for (var index = 0; index < mutations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mutation = mutations[index];
            if (mutation.Kind is not SyncMutationKind.Upsert)
            {
                RequireSafeDelete(mutation.Kind, mutation.PartitionKey);
                transformed[index] = mutation;
                continue;
            }

            var (content, tenant) = TransformJson(mutation.Content, mutation.ContentType);
            transformed[index] = new SyncMutation(
                mutation.ChangeId,
                mutation.Kind,
                mutation.Collection,
                mutation.Key,
                content,
                mutation.ContentType,
                tenant ?? mutation.PartitionKey);
        }

        return ValueTask.FromResult<IReadOnlyList<SyncMutation>>(transformed);
    }

    public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
        ChangeSnapshotBatch batch,
        IReadOnlyList<SyncSnapshotMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(mutations);
        var transformed = new SyncSnapshotMutation[mutations.Count];
        for (var index = 0; index < mutations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mutation = mutations[index];
            var (content, tenant) = TransformJson(mutation.Content, mutation.ContentType);
            transformed[index] = new SyncSnapshotMutation(
                mutation.RowId,
                mutation.Collection,
                mutation.Key,
                content,
                mutation.ContentType,
                tenant ?? mutation.PartitionKey);
        }

        return ValueTask.FromResult<IReadOnlyList<SyncSnapshotMutation>>(transformed);
    }

    private (ReadOnlyMemory<byte> Content, string? Tenant) TransformJson(
        ReadOnlyMemory<byte> content,
        string? contentType)
    {
        if (content.Length > _maximumDocumentBytes)
        {
            throw new SyncPoisonRecordException(
                $"JSON document size {content.Length} exceeds the configured limit {_maximumDocumentBytes}.");
        }

        if (contentType is null ||
            !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncPoisonRecordException(
                "The JSON transform stage requires application/json upsert content.");
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw new SyncPoisonRecordException(
                    "The JSON transform stage requires a JSON object root.");
            }

            var tenant = ResolveTenant(document.RootElement);
            var buffer = new ArrayBufferWriter<byte>(Math.Min(
                _maximumDocumentBytes,
                Math.Max(256, content.Length)));
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                WriteOriginalProperties(writer, document.RootElement);
                foreach (var entry in _enrichment)
                {
                    if (_flatten)
                    {
                        WriteFlattened(writer, entry.Key, entry.Key, entry.Value);
                    }
                    else
                    {
                        writer.WritePropertyName(entry.Key);
                        entry.Value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            if (buffer.WrittenCount > _maximumDocumentBytes)
            {
                throw new SyncPoisonRecordException(
                    $"Transformed JSON size {buffer.WrittenCount} exceeds the configured limit {_maximumDocumentBytes}.");
            }

            return (buffer.WrittenMemory.ToArray(), tenant);
        }
        catch (SyncPoisonRecordException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new SyncPoisonRecordException("The source mutation contains invalid JSON.", exception);
        }
    }

    private void WriteOriginalProperties(Utf8JsonWriter writer, JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            var path = property.Name;
            if (_redactedPaths.Contains(path) ||
                _enrichment.Any(entry => string.Equals(entry.Key, property.Name, StringComparison.Ordinal)))
            {
                continue;
            }

            if (_flatten)
            {
                EnsureFlattenableName(property.Name);
                WriteFlattened(writer, path, path, property.Value);
            }
            else
            {
                writer.WritePropertyName(property.Name);
                WriteRedacted(writer, property.Value, path);
            }
        }
    }

    private void WriteRedacted(Utf8JsonWriter writer, JsonElement element, string path)
    {
        if (element.ValueKind is not JsonValueKind.Object)
        {
            element.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject())
        {
            var childPath = path + "." + property.Name;
            if (_redactedPaths.Contains(childPath))
            {
                continue;
            }

            writer.WritePropertyName(property.Name);
            WriteRedacted(writer, property.Value, childPath);
        }

        writer.WriteEndObject();
    }

    private void WriteFlattened(
        Utf8JsonWriter writer,
        string outputPath,
        string sourcePath,
        JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Object)
        {
            var wrote = false;
            foreach (var property in element.EnumerateObject())
            {
                EnsureFlattenableName(property.Name);
                var childOutputPath = outputPath + _separator + property.Name;
                var childSourcePath = sourcePath + "." + property.Name;
                if (_redactedPaths.Contains(childSourcePath))
                {
                    continue;
                }

                wrote = true;
                WriteFlattened(writer, childOutputPath, childSourcePath, property.Value);
            }

            if (!wrote)
            {
                writer.WriteStartObject(outputPath);
                writer.WriteEndObject();
            }

            return;
        }

        writer.WritePropertyName(outputPath);
        element.WriteTo(writer);
    }

    private string? ResolveTenant(JsonElement root)
    {
        if (_tenantPath is null)
        {
            return null;
        }

        var current = root;
        foreach (var segment in _tenantPath)
        {
            if (current.ValueKind is not JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return RequireTenant();
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => RequireNonEmptyTenant(current.GetString()),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.GetRawText(),
            JsonValueKind.Null => RequireTenant(),
            _ => throw new SyncPoisonRecordException(
                "The configured tenant property must be a scalar JSON value."),
        };
    }

    private string? RequireTenant()
    {
        if (_requireTenant)
        {
            throw new SyncPoisonRecordException("The configured tenant property is missing or null.");
        }

        return null;
    }

    private string? RequireNonEmptyTenant(string? tenant)
    {
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            return tenant;
        }

        return RequireTenant();
    }

    private void RequireSafeDelete(SyncMutationKind kind, string? partitionKey)
    {
        if (_tenantPath is not null && kind is SyncMutationKind.DeleteCollection)
        {
            throw new SyncPoisonRecordException(
                "A tenant-routed transform cannot apply an unscoped collection delete.");
        }

        if (_tenantPath is not null && _requireTenant && string.IsNullOrWhiteSpace(partitionKey))
        {
            throw new SyncPoisonRecordException(
                "A routed delete mutation must retain its tenant partition key.");
        }
    }

    private void EnsureFlattenableName(string propertyName)
    {
        if (propertyName.Contains(_separator, StringComparison.Ordinal))
        {
            throw new SyncPoisonRecordException(
                $"JSON property '{propertyName}' contains the configured flatten separator.");
        }
    }

    private static SyncTransformVersion CreateVersion(
        JsonSyncTransformStageOptions options,
        IEnumerable<string> redactedPaths,
        IEnumerable<KeyValuePair<string, JsonElement>> enrichment)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", 1);
            writer.WriteBoolean("flatten", options.FlattenObjects);
            writer.WriteString("separator", options.FlattenSeparator);
            writer.WriteString("tenant", options.TenantPropertyPath);
            writer.WriteBoolean("requireTenant", options.RequireTenant);
            writer.WriteNumber("maximumDocumentBytes", options.MaximumDocumentBytes);
            writer.WriteStartArray("redacted");
            foreach (var path in redactedPaths.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();
            writer.WriteStartObject("enrichment");
            foreach (var entry in enrichment)
            {
                writer.WritePropertyName(entry.Key);
                entry.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return SyncTransformVersion.Create(options.Name, options.Version, buffer.WrittenSpan);
    }

    private static void ValidatePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        foreach (var segment in path.Split('.'))
        {
            ValidateProperty(segment, parameterName);
        }
    }

    private static void ValidateProperty(string property, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property, parameterName);
        if (property.Length > 256)
        {
            throw new ArgumentException(
                "JSON property names and path segments cannot exceed 256 characters.",
                parameterName);
        }
    }
}

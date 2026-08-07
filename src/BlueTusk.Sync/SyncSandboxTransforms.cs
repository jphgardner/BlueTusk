using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlueTusk.Streams;

namespace BlueTusk.Sync;

/// <summary>Operations supported by the deterministic Sync transformation sandbox.</summary>
public enum SyncSandboxOperation
{
    Remove,
    Set,
    Copy,
    Route,
    DropWhenEquals,
    RequireEquals,
}

/// <summary>One immutable instruction in a bounded Sync transformation program.</summary>
public sealed class SyncSandboxInstruction
{
    private SyncSandboxInstruction(
        SyncSandboxOperation operation,
        string path,
        string? sourcePath,
        string? valueJson)
    {
        Operation = operation;
        Path = path;
        SourcePath = sourcePath;
        ValueJson = valueJson;
    }

    public SyncSandboxOperation Operation { get; }

    public string Path { get; }

    public string? SourcePath { get; }

    public string? ValueJson { get; }

    public static SyncSandboxInstruction Remove(string path) =>
        new(SyncSandboxOperation.Remove, ValidatePath(path), null, null);

    public static SyncSandboxInstruction Set(string path, string valueJson) =>
        new(
            SyncSandboxOperation.Set,
            ValidatePath(path),
            null,
            NormalizeJson(valueJson));

    public static SyncSandboxInstruction Copy(string sourcePath, string targetPath) =>
        new(
            SyncSandboxOperation.Copy,
            ValidatePath(targetPath),
            ValidatePath(sourcePath),
            null);

    public static SyncSandboxInstruction Route(string path) =>
        new(SyncSandboxOperation.Route, ValidatePath(path), null, null);

    public static SyncSandboxInstruction DropWhenEquals(string path, string valueJson) =>
        new(
            SyncSandboxOperation.DropWhenEquals,
            ValidatePath(path),
            null,
            NormalizeJson(valueJson));

    public static SyncSandboxInstruction RequireEquals(string path, string valueJson) =>
        new(
            SyncSandboxOperation.RequireEquals,
            ValidatePath(path),
            null,
            NormalizeJson(valueJson));

    private static string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 2048)
        {
            throw new ArgumentException("A sandbox path cannot exceed 2,048 characters.", nameof(path));
        }

        var segments = path.Split('.');
        if (segments.Length > 64 ||
            segments.Any(static segment => string.IsNullOrWhiteSpace(segment) || segment.Length > 256))
        {
            throw new ArgumentException(
                "A sandbox path must contain between one and 64 non-empty segments of at most 256 characters.",
                nameof(path));
        }

        return path;
    }

    private static string NormalizeJson(string valueJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueJson);
        try
        {
            using var document = JsonDocument.Parse(
                valueJson,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteCanonical(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "A sandbox instruction value must contain one valid JSON value.",
                nameof(valueJson),
                exception);
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }

            writer.WriteEndObject();
            return;
        }

        if (value.ValueKind is JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                WriteCanonical(writer, item);
            }

            writer.WriteEndArray();
            return;
        }

        value.WriteTo(writer);
    }
}

/// <summary>Limits and immutable instructions for a deterministic Sync transformation sandbox.</summary>
public sealed class SyncTransformSandboxOptions
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    public required IReadOnlyList<SyncSandboxInstruction> Instructions { get; init; }

    public int MaximumMutationsPerBatch { get; init; } = 10_000;

    public int MaximumDocumentBytes { get; init; } = 1024 * 1024;

    public long MaximumBatchBytes { get; init; } = 16L * 1024 * 1024;

    public int MaximumOperationsPerBatch { get; init; } = 1_000_000;

    public int MaximumJsonDepth { get; init; } = 64;

    public TimeSpan MaximumExecutionTime { get; init; } = TimeSpan.FromSeconds(5);

    public bool RequirePartitionedDeletes { get; init; } = true;
}

/// <summary>
/// Executes a finite declarative JSON program without loading or invoking application code.
/// </summary>
public sealed class SandboxedSyncTransformStage : ISyncTransformStage
{
    private const int MaximumInstructionCount = 256;

    private readonly ReadOnlyCollection<CompiledInstruction> _instructions;
    private readonly int _maximumMutationsPerBatch;
    private readonly int _maximumDocumentBytes;
    private readonly long _maximumBatchBytes;
    private readonly int _maximumOperationsPerBatch;
    private readonly int _maximumJsonDepth;
    private readonly TimeSpan _maximumExecutionTime;
    private readonly bool _requirePartitionedDeletes;
    private readonly bool _routesDocuments;

    public SandboxedSyncTransformStage(SyncTransformSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Version);
        ArgumentNullException.ThrowIfNull(options.Instructions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumMutationsPerBatch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumBatchBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumOperationsPerBatch);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumJsonDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumJsonDepth, 256);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumExecutionTime, TimeSpan.Zero);
        if (options.Instructions.Count == 0 || options.Instructions.Count > MaximumInstructionCount)
        {
            throw new ArgumentException(
                $"A sandbox program must contain between one and {MaximumInstructionCount} instructions.",
                nameof(options));
        }

        var instructions = new CompiledInstruction[options.Instructions.Count];
        for (var index = 0; index < instructions.Length; index++)
        {
            var instruction = options.Instructions[index] ??
                throw new ArgumentException("A sandbox program cannot contain null instructions.", nameof(options));
            instructions[index] = Compile(instruction, options.MaximumJsonDepth);
        }

        _instructions = Array.AsReadOnly(instructions);
        _maximumMutationsPerBatch = options.MaximumMutationsPerBatch;
        _maximumDocumentBytes = options.MaximumDocumentBytes;
        _maximumBatchBytes = options.MaximumBatchBytes;
        _maximumOperationsPerBatch = options.MaximumOperationsPerBatch;
        _maximumJsonDepth = options.MaximumJsonDepth;
        _maximumExecutionTime = options.MaximumExecutionTime;
        _requirePartitionedDeletes = options.RequirePartitionedDeletes;
        _routesDocuments = instructions.Any(static instruction =>
            instruction.Operation is SyncSandboxOperation.Route);
        Version = CreateVersion(options, instructions);
    }

    public SyncTransformVersion Version { get; }

    public ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
        ChangeTransaction transaction,
        IReadOnlyList<SyncMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(mutations);
        var budget = CreateBudget(mutations.Count);
        var transformed = new List<SyncMutation>(mutations.Count);
        foreach (var mutation in mutations)
        {
            budget.Check(cancellationToken);
            if (mutation.Kind is not SyncMutationKind.Upsert)
            {
                ValidateDelete(mutation);
                transformed.Add(mutation);
                continue;
            }

            var result = TransformDocument(
                mutation.Content,
                mutation.ContentType,
                mutation.PartitionKey,
                budget,
                cancellationToken);
            if (result.Keep)
            {
                transformed.Add(new SyncMutation(
                    mutation.ChangeId,
                    mutation.Kind,
                    mutation.Collection,
                    mutation.Key,
                    result.Content,
                    mutation.ContentType,
                    result.PartitionKey));
            }
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
        var budget = CreateBudget(mutations.Count);
        var transformed = new List<SyncSnapshotMutation>(mutations.Count);
        foreach (var mutation in mutations)
        {
            budget.Check(cancellationToken);
            var result = TransformDocument(
                mutation.Content,
                mutation.ContentType,
                mutation.PartitionKey,
                budget,
                cancellationToken);
            if (result.Keep)
            {
                transformed.Add(new SyncSnapshotMutation(
                    mutation.RowId,
                    mutation.Collection,
                    mutation.Key,
                    result.Content,
                    mutation.ContentType,
                    result.PartitionKey));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<SyncSnapshotMutation>>(transformed);
    }

    private ExecutionBudget CreateBudget(int mutationCount)
    {
        if (mutationCount > _maximumMutationsPerBatch)
        {
            throw new SyncPoisonRecordException(
                $"Sandbox batch mutation count {mutationCount} exceeds the configured limit {_maximumMutationsPerBatch}.");
        }

        return new ExecutionBudget(
            _maximumOperationsPerBatch,
            _maximumBatchBytes,
            _maximumExecutionTime);
    }

    private DocumentResult TransformDocument(
        ReadOnlyMemory<byte> content,
        string? contentType,
        string? partitionKey,
        ExecutionBudget budget,
        CancellationToken cancellationToken)
    {
        if (content.Length > _maximumDocumentBytes)
        {
            throw new SyncPoisonRecordException(
                $"Sandbox input size {content.Length} exceeds the configured document limit {_maximumDocumentBytes}.");
        }

        if (contentType is null ||
            !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncPoisonRecordException(
                "The transformation sandbox accepts application/json upserts only.");
        }

        budget.AddBytes(content.Length);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(
                content.Span,
                nodeOptions: null,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = _maximumJsonDepth,
                }) as JsonObject ??
                throw new SyncPoisonRecordException(
                    "The transformation sandbox requires a JSON object root.");
        }
        catch (SyncPoisonRecordException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new SyncPoisonRecordException(
                "The transformation sandbox received invalid or over-depth JSON.",
                exception);
        }

        foreach (var instruction in _instructions)
        {
            budget.ExecuteOperation(cancellationToken);
            switch (instruction.Operation)
            {
                case SyncSandboxOperation.Remove:
                    Remove(root, instruction.Path);
                    break;
                case SyncSandboxOperation.Set:
                    Set(root, instruction.Path, instruction.Value!.DeepClone());
                    break;
                case SyncSandboxOperation.Copy:
                    var source = Find(root, instruction.SourcePath!);
                    if (source is null)
                    {
                        throw new SyncPoisonRecordException(
                            $"Sandbox copy source '{instruction.Source}' does not exist.");
                    }

                    Set(root, instruction.Path, source.DeepClone());
                    break;
                case SyncSandboxOperation.Route:
                    partitionKey = ScalarText(
                        Find(root, instruction.Path),
                        instruction.Source);
                    break;
                case SyncSandboxOperation.DropWhenEquals:
                    if (JsonNode.DeepEquals(Find(root, instruction.Path), instruction.Value))
                    {
                        return DocumentResult.Dropped;
                    }

                    break;
                case SyncSandboxOperation.RequireEquals:
                    if (!JsonNode.DeepEquals(Find(root, instruction.Path), instruction.Value))
                    {
                        throw new SyncPoisonRecordException(
                            $"Sandbox requirement '{instruction.Source}' did not match.");
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported sandbox operation '{instruction.Operation}'.");
            }
        }

        budget.Check(cancellationToken);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(root);
        if (encoded.Length > _maximumDocumentBytes)
        {
            throw new SyncPoisonRecordException(
                $"Sandbox output size {encoded.Length} exceeds the configured document limit {_maximumDocumentBytes}.");
        }

        budget.AddBytes(encoded.Length);
        return new DocumentResult(true, encoded, partitionKey);
    }

    private void ValidateDelete(SyncMutation mutation)
    {
        if (!_routesDocuments)
        {
            return;
        }

        if (mutation.Kind is SyncMutationKind.DeleteCollection)
        {
            throw new SyncPoisonRecordException(
                "A routing sandbox cannot safely apply an unscoped collection delete.");
        }

        if (_requirePartitionedDeletes && string.IsNullOrWhiteSpace(mutation.PartitionKey))
        {
            throw new SyncPoisonRecordException(
                "A delete passing through a routing sandbox must retain its partition key.");
        }
    }

    private static CompiledInstruction Compile(
        SyncSandboxInstruction instruction,
        int maximumJsonDepth)
    {
        var path = instruction.Path.Split('.');
        var sourcePath = instruction.SourcePath?.Split('.');
        JsonNode? value = null;
        if (instruction.ValueJson is { } valueJson)
        {
            value = JsonNode.Parse(
                valueJson,
                nodeOptions: null,
                new JsonDocumentOptions { MaxDepth = maximumJsonDepth });
        }

        return new CompiledInstruction(
            instruction.Operation,
            path,
            sourcePath,
            value,
            instruction.Operation switch
            {
                SyncSandboxOperation.Copy =>
                    $"{instruction.Operation} {instruction.SourcePath} -> {instruction.Path}",
                SyncSandboxOperation.Set or
                SyncSandboxOperation.DropWhenEquals or
                SyncSandboxOperation.RequireEquals =>
                    $"{instruction.Operation} {instruction.Path} {instruction.ValueJson}",
                _ => $"{instruction.Operation} {instruction.Path}",
            });
    }

    private static JsonNode? Find(JsonObject root, string[] path)
    {
        JsonNode? current = root;
        foreach (var segment in path)
        {
            if (current is not JsonObject objectNode ||
                !objectNode.TryGetPropertyValue(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static void Remove(JsonObject root, string[] path)
    {
        if (TryFindParent(root, path, create: false, out var parent))
        {
            _ = parent.Remove(path[^1]);
        }
    }

    private static void Set(JsonObject root, string[] path, JsonNode? value)
    {
        if (!TryFindParent(root, path, create: true, out var parent))
        {
            throw new SyncPoisonRecordException(
                $"Sandbox target parent '{string.Join('.', path.Take(path.Length - 1))}' is not a JSON object.");
        }

        parent[path[^1]] = value;
    }

    private static bool TryFindParent(
        JsonObject root,
        string[] path,
        bool create,
        out JsonObject parent)
    {
        parent = root;
        for (var index = 0; index < path.Length - 1; index++)
        {
            if (!parent.TryGetPropertyValue(path[index], out var child) || child is null)
            {
                if (!create)
                {
                    return false;
                }

                var created = new JsonObject();
                parent[path[index]] = created;
                parent = created;
                continue;
            }

            if (child is not JsonObject childObject)
            {
                return false;
            }

            parent = childObject;
        }

        return true;
    }

    private static string ScalarText(JsonNode? value, string instruction)
    {
        if (value is null)
        {
            throw new SyncPoisonRecordException(
                $"Sandbox routing instruction '{instruction}' resolved to a missing or null value.");
        }

        if (value is not JsonValue scalar)
        {
            throw new SyncPoisonRecordException(
                $"Sandbox routing instruction '{instruction}' must resolve to a scalar value.");
        }

        if (scalar.TryGetValue<string>(out var text))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new SyncPoisonRecordException(
                    $"Sandbox routing instruction '{instruction}' resolved to an empty value.");
            }

            return text;
        }

        var json = scalar.ToJsonString();
        if (json is "null")
        {
            throw new SyncPoisonRecordException(
                $"Sandbox routing instruction '{instruction}' resolved to null.");
        }

        return json;
    }

    private static SyncTransformVersion CreateVersion(
        SyncTransformSandboxOptions options,
        IEnumerable<CompiledInstruction> instructions)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", 1);
            writer.WriteNumber("maximumMutationsPerBatch", options.MaximumMutationsPerBatch);
            writer.WriteNumber("maximumDocumentBytes", options.MaximumDocumentBytes);
            writer.WriteNumber("maximumBatchBytes", options.MaximumBatchBytes);
            writer.WriteNumber("maximumOperationsPerBatch", options.MaximumOperationsPerBatch);
            writer.WriteNumber("maximumJsonDepth", options.MaximumJsonDepth);
            writer.WriteNumber("maximumExecutionTicks", options.MaximumExecutionTime.Ticks);
            writer.WriteBoolean("requirePartitionedDeletes", options.RequirePartitionedDeletes);
            writer.WriteStartArray("instructions");
            foreach (var instruction in instructions)
            {
                writer.WriteStartObject();
                writer.WriteString("operation", instruction.Operation.ToString());
                writer.WriteStartArray("path");
                foreach (var segment in instruction.Path)
                {
                    writer.WriteStringValue(segment);
                }

                writer.WriteEndArray();
                if (instruction.SourcePath is { } sourcePath)
                {
                    writer.WriteStartArray("source");
                    foreach (var segment in sourcePath)
                    {
                        writer.WriteStringValue(segment);
                    }

                    writer.WriteEndArray();
                }

                if (instruction.Value is { } value)
                {
                    writer.WritePropertyName("value");
                    value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return SyncTransformVersion.Create(options.Name, options.Version, buffer.WrittenSpan);
    }

    private sealed record CompiledInstruction(
        SyncSandboxOperation Operation,
        string[] Path,
        string[]? SourcePath,
        JsonNode? Value,
        string Source);

    private sealed class ExecutionBudget(
        int maximumOperations,
        long maximumBytes,
        TimeSpan maximumExecutionTime)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _operations;
        private long _bytes;

        public void ExecuteOperation(CancellationToken cancellationToken)
        {
            _operations++;
            if (_operations > maximumOperations)
            {
                throw new SyncPoisonRecordException(
                    $"Sandbox execution exceeded its {maximumOperations} operation budget.");
            }

            Check(cancellationToken);
        }

        public void AddBytes(int bytes)
        {
            _bytes = checked(_bytes + bytes);
            if (_bytes > maximumBytes)
            {
                throw new SyncPoisonRecordException(
                    $"Sandbox execution exceeded its {maximumBytes} byte budget.");
            }
        }

        public void Check(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(_started) > maximumExecutionTime)
            {
                throw new SyncPoisonRecordException(
                    $"Sandbox execution exceeded its {maximumExecutionTime} time budget.");
            }
        }
    }

    private readonly record struct DocumentResult(
        bool Keep,
        ReadOnlyMemory<byte> Content,
        string? PartitionKey)
    {
        public static DocumentResult Dropped { get; } =
            new(false, ReadOnlyMemory<byte>.Empty, null);
    }
}

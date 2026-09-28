using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace BlueTusk.Documents;

public enum DocumentDataSourceOwnership
{
    Borrowed,
    Owned,
}

public sealed record DocumentStoreOptions
{
    public string Schema { get; init; } = "bluetusk_documents";
    public int MaxDocumentBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxSessionOperations { get; init; } = 4096;
    public long MaxSessionBytes { get; init; } = 32L * 1024 * 1024;
    public int MaxPageSize { get; init; } = 1000;
    public long MaxPageBytes { get; init; } = 16L * 1024 * 1024;
    public int CommandsPerBatch { get; init; } = 256;
    public int CommandTimeoutSeconds { get; init; } = 30;

    internal void Validate()
    {
        _ = DocumentValidation.Identifier(Schema);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSessionOperations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSessionBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CommandsPerBatch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CommandTimeoutSeconds);
        if (MaxDocumentBytes > MaxSessionBytes || MaxDocumentBytes > MaxPageBytes)
        {
            throw new ArgumentException("Document bytes must fit within the session and page byte budgets.");
        }

        if (CommandsPerBatch > 1000 || MaxSessionOperations > 100_000 || MaxPageSize > 10_000)
        {
            throw new ArgumentException("Batch, session and page counts exceed supported bounded limits.");
        }
    }
}

public sealed class DocumentCollectionDefinition<T>
{
    public DocumentCollectionDefinition(string name, JsonTypeInfo<T> jsonTypeInfo, int schemaVersion = 1)
    {
        DocumentValidation.Key(name, nameof(name), 256);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        Name = name;
        JsonTypeInfo = jsonTypeInfo;
        SchemaVersion = schemaVersion;
    }

    public string Name { get; }
    public JsonTypeInfo<T> JsonTypeInfo { get; }
    public int SchemaVersion { get; }
}

public sealed record StoredDocument<T>(string Id, T Value, long Revision, int SchemaVersion);
public sealed record DocumentWriteResult(string Collection, string Id, long? Revision);
public sealed record DocumentPage<T>(IReadOnlyList<StoredDocument<T>> Items, string? NextAfterId);
public sealed record DocumentMigrationPage(int MigratedCount, string? NextAfterId);

/// <summary>A deterministic transform from stored JSON to the target typed schema. Perform external side effects after commit.</summary>
public sealed class DocumentMigration<T>
{
    public DocumentMigration(int fromVersion, int toVersion, Func<JsonElement, T> transform)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromVersion);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(toVersion, fromVersion);
        ArgumentNullException.ThrowIfNull(transform);
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Transform = transform;
    }

    public int FromVersion { get; }
    public int ToVersion { get; }
    public Func<JsonElement, T> Transform { get; }
}

/// <summary>The transformed typed body and detached bytes produced from one inline JSON document.</summary>
public sealed class DocumentInlineContentResult<T>
{
    public DocumentInlineContentResult(T value, ReadOnlyMemory<byte> content)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        Content = content;
    }

    public T Value { get; }
    public ReadOnlyMemory<byte> Content { get; }
}

/// <summary>A deterministic inline-content extraction for one explicit schema-version upgrade.</summary>
public sealed class DocumentInlineContentMigration<T>
{
    public DocumentInlineContentMigration(int fromVersion, int toVersion, Func<JsonElement, DocumentInlineContentResult<T>> transform)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromVersion);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(toVersion, fromVersion);
        ArgumentNullException.ThrowIfNull(transform);
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Transform = transform;
    }

    public int FromVersion { get; }
    public int ToVersion { get; }
    public Func<JsonElement, DocumentInlineContentResult<T>> Transform { get; }
}

public sealed class DocumentSchemaVersionException : Exception
{
    public DocumentSchemaVersionException(string collection, string id, int requestedVersion, int actualVersion)
        : base($"Document '{collection}/{id}' has schema version {actualVersion}; the requested write uses schema version {requestedVersion}.")
    {
        Collection = collection;
        Id = id;
        RequestedVersion = requestedVersion;
        ActualVersion = actualVersion;
    }

    public string Collection { get; }
    public string Id { get; }
    public int RequestedVersion { get; }
    public int ActualVersion { get; }
}

public sealed class DocumentConcurrencyException : Exception
{
    public DocumentConcurrencyException(string tenant, string collection, string id, long? expectedRevision, long? actualRevision)
        : base(expectedRevision is null
            ? $"Document '{collection}/{id}' already exists in tenant '{tenant}'."
            : $"Document '{collection}/{id}' in tenant '{tenant}' no longer has revision {expectedRevision} (current: {actualRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}).")
    {
        Tenant = tenant;
        Collection = collection;
        Id = id;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string Tenant { get; }
    public string Collection { get; }
    public string Id { get; }
    public long? ExpectedRevision { get; }
    public long? ActualRevision { get; }
}

public enum DocumentPatchKind
{
    Set,
    Remove,
}

public sealed class DocumentPatch
{
    private DocumentPatch(DocumentPatchKind kind, string[] path, string? jsonValue)
    {
        Kind = kind;
        Path = Array.AsReadOnly(path);
        JsonValue = jsonValue;
        PathJson = JsonSerializer.Serialize(path, DocumentJsonContext.Default.StringArray);
    }

    public DocumentPatchKind Kind { get; }
    public IReadOnlyList<string> Path { get; }
    public string? JsonValue { get; }
    internal string PathJson { get; }

    public static DocumentPatch Set(IReadOnlyList<string> path, JsonElement value) =>
        new(DocumentPatchKind.Set, ValidatePath(path), value.GetRawText());

    public static DocumentPatch Remove(IReadOnlyList<string> path) =>
        new(DocumentPatchKind.Remove, ValidatePath(path), null);

    private static string[] ValidatePath(IReadOnlyList<string> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count is 0 or > 32)
        {
            throw new ArgumentException("A patch path requires between 1 and 32 segments.", nameof(path));
        }

        var copy = new string[path.Count];
        for (var i = 0; i < path.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(path[i]);
            if (path[i].Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(path[i]) > 512)
            {
                throw new ArgumentException("Patch path segments cannot contain NUL and must fit within 512 UTF-8 bytes.", nameof(path));
            }

            copy[i] = path[i];
        }

        return copy;
    }
}

public enum DocumentIndexKind
{
    JsonContainment,
    TextPath,
}

public sealed class DocumentIndexDefinition
{
    public DocumentIndexDefinition(string name, DocumentIndexKind kind, string? collection = null, IReadOnlyList<string>? path = null)
    {
        _ = DocumentValidation.Identifier(name);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (collection is not null)
        {
            DocumentValidation.Key(collection, nameof(collection), 256);
        }

        if (kind is DocumentIndexKind.TextPath && (collection is null || path is null))
        {
            throw new ArgumentException("A text path index requires a collection and path.");
        }

        if (kind is DocumentIndexKind.JsonContainment && path is not null)
        {
            throw new ArgumentException("A containment index does not accept a path.", nameof(path));
        }

        Name = name;
        Kind = kind;
        Collection = collection;
        Path = path is null ? null : DocumentPatch.Remove(path).Path;
    }

    public string Name { get; }
    public DocumentIndexKind Kind { get; }
    public string? Collection { get; }
    public IReadOnlyList<string>? Path { get; }
}

internal static class DocumentValidation
{
    internal static void Key(string value, string parameterName, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(value) > maxBytes)
        {
            throw new ArgumentException($"Value must fit within {maxBytes} UTF-8 bytes and cannot contain NUL.", parameterName);
        }
    }

    internal static string Identifier(string value)
    {
        Key(value, nameof(value), 63);
        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    internal static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

[JsonSerializable(typeof(string[]))]
internal sealed partial class DocumentJsonContext : JsonSerializerContext;

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueTusk.Streams;

namespace BlueTusk.Documents.Streams;

public sealed record DocumentStreamOptions
{
    public string Schema { get; init; } = "bluetusk_documents";
    public int MaxTransactionChanges { get; init; } = 4096;
    public long MaxTransactionBytes { get; init; } = 32L * 1024 * 1024;
    public int MaxDocumentBytes { get; init; } = 4 * 1024 * 1024;
    public bool RequireCompleteNewDocuments { get; init; }
}

/// <summary>Retains raw column states when replica identity, publication columns or TOAST omit an image.</summary>
public sealed record DocumentChangeImage<T>(string Tenant, string Collection, string Id, long? Revision, int? SchemaVersion, T? Value, bool HasValue, ChangeColumnState BodyState, ChangeRow Columns);
public sealed record DocumentStreamChange<T>(ChangeId Id, ChangeKind Kind, DocumentChangeImage<T>? OldImage, DocumentChangeImage<T>? NewImage);
public sealed record DocumentStreamTransaction<T>(ChangeTransaction SourceTransaction, IReadOnlyList<DocumentStreamChange<T>> Changes);
public sealed record DocumentStreamSnapshot<T>(SnapshotEpoch Epoch, long Sequence, bool IsLastForTable, IReadOnlyList<DocumentChangeImage<T>> Documents);
public sealed class DocumentStreamMappingException(string message) : Exception(message);
public sealed class DocumentStreamResetRequiredException() : Exception("The document table was truncated; discard the derived view and request a new consistent snapshot.");

/// <summary>Maps committed WAL images without fetching a later database version. Keys and the original causal IDs are retained.</summary>
public sealed class DocumentChangeMapper<T>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly DocumentCollectionDefinition<T> _collection;
    private readonly string _tenant;
    private readonly DocumentStreamOptions _options;

    public DocumentChangeMapper(string tenant, DocumentCollectionDefinition<T> collection, DocumentStreamOptions? options = null)
    {
        ValidateKey(tenant, 256);
        ArgumentNullException.ThrowIfNull(collection);
        _options = options ?? new DocumentStreamOptions();
        ValidateKey(_options.Schema, 63);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxTransactionChanges, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaxTransactionChanges, 100_000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxTransactionBytes, _options.MaxDocumentBytes);
        _tenant = tenant;
        _collection = collection;
    }

    public async ValueTask<DocumentStreamTransaction<T>> MapTransactionAsync(ChangeTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();
        if (transaction.Outcome is not ChangeTransactionOutcome.Committed || transaction.GlobalTransactionId is not null)
        {
            throw new DocumentStreamMappingException("Prepared transaction lifecycle delivery requires a durable staging consumer before document mapping.");
        }

        if (transaction.Changes.Count > _options.MaxTransactionChanges || transaction.Changes.EstimatedBytes > _options.MaxTransactionBytes)
        {
            throw new DocumentStreamMappingException("The source transaction exceeds the bounded document mapping budget.");
        }

        var changes = new List<DocumentStreamChange<T>>();
        long bytes = 0;
        var count = 0;
        await foreach (var change in transaction.Changes.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (++count > _options.MaxTransactionChanges)
            {
                throw new DocumentStreamMappingException("The source transaction exceeds the bounded change count.");
            }

            switch (change)
            {
                case InsertChange insert when Matches(insert.NewRow.Table):
                    AddImageBytes(insert.NewRow, ref bytes);
                    var inserted = MapImage(insert.NewRow, null, isNew: true);
                    if (InScope(inserted)) { changes.Add(new(change.Id, ChangeKind.Insert, null, inserted)); }
                    break;
                case UpdateChange update when Matches(update.NewRow.Table):
                    AddImageBytes(update.OldRow, ref bytes);
                    AddImageBytes(update.NewRow, ref bytes);
                    var previous = MapImage(update.OldRow, update.NewRow, isNew: false);
                    var current = MapImage(update.NewRow, update.OldRow, isNew: true);
                    var before = InScope(previous) ? previous : null;
                    var after = InScope(current) ? current : null;
                    if (before is not null || after is not null)
                    {
                        // A key moving across a tenant or collection boundary becomes a removal/addition within this scope.
                        var kind = before is null ? ChangeKind.Insert : after is null ? ChangeKind.Delete : ChangeKind.Update;
                        changes.Add(new(change.Id, kind, before, after));
                    }
                    break;
                case DeleteChange delete when Matches(delete.OldRow.Table):
                    AddImageBytes(delete.OldRow, ref bytes);
                    var removed = MapImage(delete.OldRow, null, isNew: false);
                    if (InScope(removed)) { changes.Add(new(change.Id, ChangeKind.Delete, removed, null)); }
                    break;
                case TruncateChange truncate when truncate.Tables.Any(Matches):
                    throw new DocumentStreamResetRequiredException();
            }
        }

        return new(transaction, changes.AsReadOnly());
    }

    public DocumentStreamSnapshot<T> MapSnapshot(ChangeSnapshotBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(batch.Table))
        {
            return new(batch.Epoch, batch.Sequence, batch.IsLastForTable, Array.Empty<DocumentChangeImage<T>>());
        }

        if (batch.Rows.Count > _options.MaxTransactionChanges)
        {
            throw new DocumentStreamMappingException("The snapshot batch exceeds the bounded change count.");
        }

        var documents = new List<DocumentChangeImage<T>>();
        long bytes = 0;
        foreach (var row in batch.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddImageBytes(row.Row, ref bytes);
            var image = MapImage(row.Row, null, isNew: true);
            if (InScope(image)) { documents.Add(image); }
        }

        return new(batch.Epoch, batch.Sequence, batch.IsLastForTable, documents.AsReadOnly());
    }

    private DocumentChangeImage<T> MapImage(ChangeRow row, ChangeRow? fallback, bool isNew)
    {
        var tenant = KeyText(row, fallback, "tenant", isNew);
        var collection = KeyText(row, fallback, "collection", isNew);
        var id = KeyText(row, fallback, "id", isNew);
        ValidateKey(tenant, 256);
        ValidateKey(collection, 256);
        ValidateKey(id, 512);
        var revision = Number(row["revision"], 8);
        var schemaNumber = Number(row["schema_version"], 4);
        var schemaVersion = schemaNumber is null ? (int?)null : checked((int)schemaNumber.Value);
        var body = row["body"];
        var resolvedBody = body.State is ChangeColumnState.UnchangedToast && fallback is not null ? fallback["body"] : body;
        T? value = default;
        var hasValue = false;
        // Do not deserialize a historical schema using a different typed contract.
        if (InScope(tenant, collection) && schemaVersion == _collection.SchemaVersion && resolvedBody.State is ChangeColumnState.Value)
        {
            var json = JsonBytes(resolvedBody);
            if (json.Length > _options.MaxDocumentBytes) { throw new DocumentStreamMappingException("Document image exceeds the byte budget."); }
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind is not JsonValueKind.Object) { throw new DocumentStreamMappingException("A document image must be a JSON object."); }
            value = JsonSerializer.Deserialize(json.Span, _collection.JsonTypeInfo);
            hasValue = value is not null;
        }

        if (isNew && InScope(tenant, collection) && _options.RequireCompleteNewDocuments && (!hasValue || revision is null || schemaVersion != _collection.SchemaVersion))
        {
            throw new DocumentStreamMappingException("A complete matching-schema new image is required; publish all document columns and use REPLICA IDENTITY FULL when unchanged TOAST needs an old value.");
        }

        return new(tenant, collection, id, revision, schemaVersion, value, hasValue, body.State, row);
    }

    private void AddImageBytes(ChangeRow row, ref long bytes)
    {
        foreach (var value in row.Values)
        {
            bytes = checked(bytes + value.Data.Length);
            if (bytes > _options.MaxTransactionBytes) { throw new DocumentStreamMappingException("The source transaction exceeds the bounded byte budget."); }
        }
    }

    private bool Matches(ChangeTable table) => table.Schema == _options.Schema && table.Name == "documents";
    private bool InScope(DocumentChangeImage<T> image) => InScope(image.Tenant, image.Collection);
    private bool InScope(string tenant, string collection) => tenant == _tenant && collection == _collection.Name;

    private static string Text(ChangeColumnValue value)
    {
        if (value.State is not ChangeColumnState.Value || value.Encoding is not (ChangeValueEncoding.Text or ChangeValueEncoding.Binary))
        {
            throw new DocumentStreamMappingException("Published document keys must contain text values.");
        }

        return StrictUtf8.GetString(value.Data.Span);
    }

    private static string KeyText(ChangeRow row, ChangeRow? fallback, string name, bool isNew)
    {
        var value = row[name];
        // pgoutput omits the old tuple when the replica key did not change. Its unavailable old key is then the new key.
        if (!isNew && value.State is ChangeColumnState.OldValueUnavailable && fallback is not null) { value = fallback[name]; }
        return Text(value);
    }

    private static long? Number(ChangeColumnValue value, int binaryLength)
    {
        if (value.State is ChangeColumnState.NotPublished or ChangeColumnState.OldValueUnavailable) { return null; }
        long number;
        if (value.State is not ChangeColumnState.Value) { throw new DocumentStreamMappingException("Document revision/schema values must be positive or explicitly unavailable."); }
        if (value.Encoding is ChangeValueEncoding.Binary && value.Data.Length == binaryLength)
        {
            number = binaryLength == 8 ? BinaryPrimitives.ReadInt64BigEndian(value.Data.Span) : BinaryPrimitives.ReadInt32BigEndian(value.Data.Span);
        }
        else if (value.Encoding is ChangeValueEncoding.Text && long.TryParse(Text(value), NumberStyles.None, CultureInfo.InvariantCulture, out number)) { }
        else { throw new DocumentStreamMappingException("The document revision/schema encoding is invalid."); }
        if (number <= 0) { throw new DocumentStreamMappingException("Document revision/schema values must be positive."); }
        return number;
    }

    private static ReadOnlyMemory<byte> JsonBytes(ChangeColumnValue value)
    {
        if (value.Encoding is ChangeValueEncoding.Text) { return value.Data; }
        if (value.Encoding is ChangeValueEncoding.Binary && value.Data.Length > 1 && value.Data.Span[0] == 1) { return value.Data[1..]; }
        throw new DocumentStreamMappingException("The JSONB image encoding or binary version is unsupported.");
    }

    private static void ValidateKey(string value, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(value) > maxBytes)
        {
            throw new ArgumentException("Document scope/key exceeds its UTF-8 byte budget or contains NUL.", nameof(value));
        }
    }
}

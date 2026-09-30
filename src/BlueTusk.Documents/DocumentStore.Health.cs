namespace BlueTusk.Documents;

public sealed class DocumentStoreHealthSnapshot
{
    internal DocumentStoreHealthSnapshot(DateTimeOffset databaseTime, int storageVersion, int maxDocumentBytes, bool hasDocuments)
    { DatabaseTime = databaseTime; StorageVersion = storageVersion; MaxDocumentBytes = maxDocumentBytes; HasDocuments = hasDocuments; }
    public DateTimeOffset DatabaseTime { get; }
    public int StorageVersion { get; }
    public int MaxDocumentBytes { get; }
    public bool HasDocuments { get; }
}

public sealed partial class DocumentStore
{
    /// <summary>Checks installed storage and one indexed host-selected scope without reading payloads, initializing or repairing state.</summary>
    public async ValueTask<DocumentStoreHealthSnapshot> ReadHealthAsync(string tenant, string collection, CancellationToken cancellationToken = default)
    {
        DocumentValidation.Key(tenant, nameof(tenant), 256); DocumentValidation.Key(collection, nameof(collection), 256);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            SELECT clock_timestamp(),storage_version,max_document_bytes,
              EXISTS(SELECT 1 FROM {DocumentsTable} WHERE tenant=@tenant AND collection=@collection)
            FROM {QuotedSchema}.storage_metadata WHERE singleton
            """);
        AddParameter(command, "tenant", tenant); AddParameter(command, "collection", collection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(1) != CurrentStorageVersion || reader.GetInt32(2) != Options.MaxDocumentBytes)
        { throw new InvalidOperationException("The installed Documents storage version or document byte budget differs."); }
        return new(reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetBoolean(3));
    }
}

namespace BlueTusk.Sync.OpenSearch;

/// <summary>Compares the document count of one active and rebuilding collection.</summary>
/// <param name="Collection">The logical collection name.</param>
/// <param name="ActiveDocuments">The active generation document count.</param>
/// <param name="RebuildDocuments">The rebuilding generation document count.</param>
public sealed record OpenSearchCollectionCount(
    string Collection,
    long ActiveDocuments,
    long RebuildDocuments)
{
    /// <summary>Gets whether both generations contain the same number of documents.</summary>
    public bool IsMatch => ActiveDocuments == RebuildDocuments;
}

/// <summary>Describes the count verification performed before an alias cutover.</summary>
public sealed class OpenSearchRebuildVerification
{
    private readonly IReadOnlyList<OpenSearchCollectionCount> _collections;

    /// <summary>Initializes a verified collection set.</summary>
    public OpenSearchRebuildVerification(IEnumerable<OpenSearchCollectionCount> collections)
    {
        ArgumentNullException.ThrowIfNull(collections);
        _collections = Array.AsReadOnly(collections.ToArray());
    }

    /// <summary>Gets the per-collection comparisons.</summary>
    public IReadOnlyList<OpenSearchCollectionCount> Collections => _collections;

    /// <summary>Gets whether every collection count matches.</summary>
    public bool IsMatch => _collections.All(collection => collection.IsMatch);
}

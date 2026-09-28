using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueTusk.Live;

namespace BlueTusk.Documents.Live;

public static class DocumentLiveQuery
{
    /// <summary>Builds a bounded ID-ordered JSONB containment query. Resolve the tenant from the server's authenticated security scope.</summary>
    public static LiveQueryPlan<StoredDocument<T>, string> Create<T>(
        DocumentStore store,
        DocumentCollectionDefinition<T> collection,
        string name,
        string databaseIdentity,
        string version,
        Func<LiveSecurityScope, string> authenticatedTenant,
        int maximumResults = 100,
        JsonElement? contains = null,
        string? afterId = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(authenticatedTenant);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, store.Options.MaxPageSize);
        if (contains is not null && contains.Value.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("Document containment must be a JSON object.", nameof(contains));
        }

        var filter = contains?.Clone();
        var canonical = Encoding.UTF8.GetBytes(string.Join('\0', store.Options.Schema, collection.Name,
            collection.SchemaVersion.ToString(CultureInfo.InvariantCulture), maximumResults.ToString(CultureInfo.InvariantCulture),
            afterId ?? string.Empty, filter?.GetRawText() ?? string.Empty));
        var fingerprint = LiveQueryFingerprint.Create(name, version, canonical);
        return new LiveQueryPlan<StoredDocument<T>, string>(name, databaseIdentity, fingerprint,
            LiveQueryCapabilities.SingleTable | LiveQueryCapabilities.TenantFilter | LiveQueryCapabilities.ParameterizedPredicate |
            LiveQueryCapabilities.DeterministicOrdering | LiveQueryCapabilities.BoundedTake,
            [new LiveTableDependency(store.Options.Schema, "documents")], [], maximumResults,
            async (context, cancellationToken) =>
            {
                var tenant = authenticatedTenant(context.SecurityScope);
                return (await store.ReadPageAsync(tenant, collection, maximumResults, afterId, filter, cancellationToken).ConfigureAwait(false)).Items;
            }, static document => document.Id, new DocumentRevisionComparer<T>(), StringComparer.Ordinal);
    }

    private sealed class DocumentRevisionComparer<T> : IEqualityComparer<StoredDocument<T>>
    {
        public bool Equals(StoredDocument<T>? left, StoredDocument<T>? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null && left.Id == right.Id && left.Revision == right.Revision && left.SchemaVersion == right.SchemaVersion;
        public int GetHashCode(StoredDocument<T> value) => HashCode.Combine(value.Id, value.Revision, value.SchemaVersion);
    }
}

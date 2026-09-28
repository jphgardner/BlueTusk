using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Schema;

/// <summary>A reviewed concurrent build for one new ordinary-table index.</summary>
public sealed class SchemaConcurrentIndexBuild
{
    internal SchemaConcurrentIndexBuild(SchemaRelationIdentity relation, string indexName,
        string expectedDefinition, string createSql)
    { Relation = relation; IndexName = indexName; ExpectedDefinition = expectedDefinition; CreateSql = createSql; }

    public SchemaRelationIdentity Relation { get; }
    public string IndexName { get; }
    public string ExpectedDefinition { get; }
    public string CreateSql { get; }
}

/// <summary>Bounded additive index declarations; changed and removed indexes require a separate reviewed migration.</summary>
public sealed class SchemaConcurrentIndexPlan
{
    private SchemaConcurrentIndexPlan(string before, string after, SchemaConcurrentIndexBuild[] builds)
    {
        BeforeFingerprint = before; AfterFingerprint = after; Builds = Array.AsReadOnly(builds);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        Add("BlueTusk.Schema.ConcurrentIndexPlan:1"); Add(before); Add(after);
        foreach (var build in builds)
        {
            Add(build.Relation.Schema); Add(build.Relation.Name); Add(build.IndexName);
            Add(build.ExpectedDefinition); Add(build.CreateSql);
        }
        Fingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
        ActionContent = "BlueTusk.Schema.ConcurrentIndexBuild:1:" + Fingerprint;
    }

    public string BeforeFingerprint { get; }
    public string AfterFingerprint { get; }
    public string Fingerprint { get; }
    public string ActionContent { get; }
    public ReadOnlyCollection<SchemaConcurrentIndexBuild> Builds { get; }

    public static SchemaConcurrentIndexPlan Create(SchemaCatalogSnapshot before, SchemaCatalogSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        var beforeIndex = Index(before.Relations); var afterIndex = Index(after.Relations);
        foreach (var pair in beforeIndex)
        {
            if (!afterIndex.TryGetValue(pair.Key, out var target) ||
                pair.Value.Relation.Identity != target.Relation.Identity ||
                pair.Value.Index.Definition != target.Index.Definition || pair.Value.Index.IsValid != target.Index.IsValid)
            { throw new InvalidOperationException("Removed, changed or repaired indexes require a separate reviewed deployment."); }
        }
        var builds = new List<SchemaConcurrentIndexBuild>();
        long bytes = 0;
        foreach (var pair in afterIndex.OrderBy(value => value.Key.Schema, StringComparer.Ordinal)
            .ThenBy(value => value.Key.IndexName, StringComparer.Ordinal))
        {
            if (beforeIndex.ContainsKey(pair.Key)) { continue; }
            var (relation, index) = pair.Value;
            if (relation.Kind != "r" || !index.IsValid)
            { throw new InvalidOperationException("Concurrent builds require a valid target index on an ordinary table or leaf partition."); }
            var sql = ConcurrentSql(index.Definition);
            bytes += Encoding.UTF8.GetByteCount(sql) + Encoding.UTF8.GetByteCount(index.Definition) + 128;
            if (builds.Count >= 1024 || bytes > 32 * 1024 * 1024)
            { throw new SchemaCaptureLimitException(); }
            builds.Add(new(relation.Identity, index.Name, index.Definition, sql));
        }
        if (builds.Count == 0) { throw new ArgumentException("No new indexes were declared.", nameof(after)); }
        return new(before.Fingerprint, after.Fingerprint, builds.ToArray());
    }

    private static Dictionary<(string Schema, string IndexName), (SchemaRelation Relation, SchemaIndex Index)> Index(
        SchemaSnapshot snapshot)
    {
        var result = new Dictionary<(string Schema, string IndexName), (SchemaRelation, SchemaIndex)>();
        foreach (var relation in snapshot.Relations)
        foreach (var index in relation.Indexes)
        {
            if (!result.TryAdd((relation.Identity.Schema, index.Name), (relation, index)))
            { throw new ArgumentException("Index names must be unique within a PostgreSQL schema.", nameof(snapshot)); }
        }
        return result;
    }

    private static string ConcurrentSql(string definition)
    {
        const string regular = "CREATE INDEX ";
        const string unique = "CREATE UNIQUE INDEX ";
        var prefix = definition.StartsWith(regular, StringComparison.Ordinal) ? regular :
            definition.StartsWith(unique, StringComparison.Ordinal) ? unique : null;
        if (prefix is null || definition.Length > 1024 * 1024 ||
            definition.AsSpan(prefix.Length).StartsWith("IF NOT EXISTS ", StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("A captured CREATE INDEX definition without IF NOT EXISTS is required.", nameof(definition)); }
        return prefix[..^1] + " CONCURRENTLY " + definition[prefix.Length..];
    }
}

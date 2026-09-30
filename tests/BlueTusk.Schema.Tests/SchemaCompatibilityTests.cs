namespace BlueTusk.Schema.Tests;

public sealed class SchemaCompatibilityTests
{
    private static readonly SchemaRelationIdentity Orders = new("app", "orders");
    private static readonly string[] ExpectedImpactedConsumers = ["labels", "whole-row"];

    [Fact]
    public void Fingerprints_are_canonical_and_length_delimited()
    {
        var columns = new[] { new SchemaColumn("id", 1, "bigint", false), new SchemaColumn("name", 2, "text", true) };
        var first = new SchemaSnapshot([Relation(columns), new(new("app", "other"), "r", false, false, "d", columns)]);
        var reversed = new SchemaSnapshot([new(new("app", "other"), "r", false, false, "d", columns.Reverse()), Relation(columns.Reverse())]);
        Assert.Equal(first.Fingerprint, reversed.Fingerprint);
        Assert.Equal(64, first.Fingerprint.Length);
        var a = new SchemaSnapshot([new(new("a/b", "c"), "r", false, false, "d", columns)]);
        var b = new SchemaSnapshot([new(new("a", "b/c"), "r", false, false, "d", columns)]);
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void Snapshot_does_not_retain_caller_mutable_collections()
    {
        var columns = new List<SchemaColumn> { new("id", 1, "bigint", false) };
        var relation = Relation(columns);
        var snapshot = new SchemaSnapshot([relation]);
        var fingerprint = snapshot.Fingerprint;
        columns.Add(new("secret", 2, "text", true));
        Assert.Single(relation.Columns);
        Assert.Equal(fingerprint, snapshot.Fingerprint);
    }

    [Fact]
    public void Duplicate_relation_and_column_identities_are_rejected()
    {
        var relation = Relation([new("id", 1, "bigint", false)]);
        Assert.Throws<ArgumentException>(() => new SchemaSnapshot([relation, relation]));
        Assert.Throws<ArgumentException>(() => Relation([new("id", 1, "bigint", false), new("id", 2, "text", true)]));
        Assert.Throws<ArgumentException>(() => Relation([new("id", 1, "bigint", false), new("other", 1, "text", true)]));
    }

    [Fact]
    public void Column_additions_classify_reader_and_writer_compatibility()
    {
        var before = new SchemaSnapshot([Relation([new("id", 1, "bigint", false)])]);
        var after = new SchemaSnapshot([Relation([
            new("id", 1, "bigint", false), new("optional", 2, "text", true),
            new("required", 3, "text", false), new("with_default", 4, "text", false, "'hello'::text")])]);
        var result = SchemaCompatibility.Compare(before, after);
        Assert.True(result.HasIncompatibleChanges);
        Assert.Contains(result.Changes, change => change.Member == "optional" && change.Impact == SchemaChangeImpact.Additive);
        Assert.Contains(result.Changes, change => change.Member == "required" && change.Impact == SchemaChangeImpact.Incompatible);
        Assert.Contains(result.Changes, change => change.Member == "with_default" && change.Impact == SchemaChangeImpact.Additive);
    }

    [Fact]
    public void Explicit_consumer_dependencies_filter_unrelated_column_changes()
    {
        var before = new SchemaSnapshot([Relation([new("id", 1, "bigint", false), new("label", 2, "text", true)])]);
        var after = new SchemaSnapshot([Relation([new("id", 1, "bigint", false)])]);
        var comparison = SchemaCompatibility.Compare(before, after);
        var impacts = SchemaCompatibility.AnalyzeConsumers(comparison,
            [new("ids", Orders, ["id"]), new("labels", Orders, ["label"]), new("whole-row", Orders, [], true)]);
        Assert.Equal(ExpectedImpactedConsumers, impacts.Select(impact => impact.Consumer));
    }

    [Fact]
    public void Policy_and_view_definition_changes_change_fingerprint_and_require_review()
    {
        var before = new SchemaSnapshot([new(Orders, "v", true, false, "d", [new("id", 1, "bigint", false)],
            policies: [new("tenant", "*", true, "PUBLIC", "tenant_id = 1", null)], definitionSql: "SELECT 1 AS id")]);
        var after = new SchemaSnapshot([new(Orders, "v", true, false, "d", [new("id", 1, "bigint", false)],
            policies: [new("tenant", "*", true, "PUBLIC", "tenant_id = 2", null)], definitionSql: "SELECT 2 AS id")]);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        var comparison = SchemaCompatibility.Compare(before, after);
        Assert.True(comparison.RequiresReview);
        Assert.Contains(comparison.Changes, change => change.Kind == SchemaChangeKind.PolicyChanged);
        Assert.Contains(comparison.Changes, change => change.Kind == SchemaChangeKind.RelationDefinitionChanged);
    }

    [Fact]
    public void Identical_snapshots_have_no_changes()
    {
        var snapshot = new SchemaSnapshot([Relation([new("id", 1, "bigint", false)])]);
        Assert.Empty(SchemaCompatibility.Compare(snapshot, snapshot).Changes);
    }

    [Fact]
    public void Sorted_relation_merge_detects_additions_removals_and_changes_across_schema_boundaries()
    {
        var before = new SchemaSnapshot([
            new(new("a", "removed"), "r", false, false, "d", []),
            new(new("z", "same"), "r", false, false, "d", [new("id", 1, "bigint", false)])]);
        var after = new SchemaSnapshot([
            new(new("a", "added"), "r", false, false, "d", []),
            new(new("z", "same"), "r", false, false, "d", [new("id", 1, "text", false)]),
            new(new("zz", "last"), "r", false, false, "d", [])]);
        var comparison = SchemaCompatibility.Compare(before, after);
        Assert.Equal(4, comparison.Changes.Count);
        Assert.Contains(comparison.Changes, change => change.Relation.Name == "removed" && change.Kind == SchemaChangeKind.RelationRemoved);
        Assert.Contains(comparison.Changes, change => change.Relation.Name == "added" && change.Kind == SchemaChangeKind.RelationAdded);
        Assert.Contains(comparison.Changes, change => change.Relation.Name == "same" && change.Kind == SchemaChangeKind.ColumnTypeChanged);
        Assert.Contains(comparison.Changes, change => change.Relation.Name == "last" && change.Kind == SchemaChangeKind.RelationAdded);
    }

    [Fact]
    public void Persisted_snapshots_round_trip_and_reject_tampering_and_future_versions()
    {
        var snapshot = new SchemaSnapshot([Relation([new("id", 1, "bigint", false)])]);
        var bytes = SchemaSnapshotSerializer.Serialize(snapshot);
        var restored = SchemaSnapshotSerializer.Deserialize(bytes);
        Assert.Equal(snapshot.Fingerprint, restored.Fingerprint);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Throws<System.Text.Json.JsonException>(() => SchemaSnapshotSerializer.Deserialize(
            System.Text.Encoding.UTF8.GetBytes(text.Replace("bigint", "smallint", StringComparison.Ordinal))));
        Assert.Throws<System.Text.Json.JsonException>(() => SchemaSnapshotSerializer.Deserialize(
            System.Text.Encoding.UTF8.GetBytes(text.Replace("\"FormatVersion\":1", "\"FormatVersion\":2", StringComparison.Ordinal))));
    }

    private static SchemaRelation Relation(IEnumerable<SchemaColumn> columns) => new(Orders, "r", false, false, "d", columns);
}

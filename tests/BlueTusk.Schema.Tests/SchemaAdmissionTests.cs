using System.Text;
using System.Text.Json;

namespace BlueTusk.Schema.Tests;

public sealed class SchemaAdmissionTests
{
    private static readonly SchemaRelationIdentity Identity = new("app", "items");

    [Fact]
    public void Null_members_are_rejected_before_sorting()
    {
        Assert.Throws<ArgumentException>(() => new SchemaSnapshot([null!]));
        Assert.Throws<ArgumentException>(() => Relation([null!]));
        Assert.Throws<ArgumentException>(() => new SchemaRelation(Identity, "r", false, false, "d", [], constraints: [null!]));
        Assert.Throws<ArgumentException>(() => new SchemaRelation(Identity, "r", false, false, "d", [], indexes: [null!]));
        Assert.Throws<ArgumentException>(() => new SchemaRelation(Identity, "r", false, false, "d", [], policies: [null!]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\0name")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Invalid_identifiers_are_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => Relation([new(name, 1, "text", true)]));
        Assert.Throws<ArgumentException>(() => new SchemaRelation(new("app", name), "r", false, false, "d", []));
        Assert.Throws<ArgumentException>(() => new SchemaRelation(Identity, "r", false, false, "d", [], constraints: [new(name, "c", "CHECK (true)")]));
        Assert.Throws<ArgumentException>(() => new SchemaRelation(Identity, "r", false, false, "d", [], indexes: [new(name, "CREATE INDEX x", true)]));
        Assert.Throws<ArgumentException>(() => new SchemaRelation(Identity, "r", false, false, "d", [], policies: [new(name, "*", true, "PUBLIC", null, null)]));
    }

    [Fact]
    public void Quoted_whitespace_and_multibyte_identifiers_remain_valid()
    {
        var relation = new SchemaRelation(new(" ", "表"), "r", false, false, "d", [new(" ", 1, "text", true)]);
        Assert.Equal("表", SchemaSnapshotSerializer.Deserialize(SchemaSnapshotSerializer.Serialize(new([relation]))).Relations[0].Identity.Name);
    }

    [Fact]
    public void Invalid_unicode_is_rejected_before_fingerprinting()
    {
        var name = new string((char)0xd800, 1);
        Assert.Throws<ArgumentException>(() => Relation([new(name, 1, "text", true)]));
        Assert.Throws<ArgumentException>(() => new SchemaRelation(Identity, "r", false, false, "d", [], definitionSql: name));
    }

    [Fact]
    public void Invalid_catalogue_flags_and_required_metadata_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SchemaRelation(Identity, "unknown", false, false, "d", []));
        Assert.Throws<ArgumentException>(() => new SchemaRelation(Identity, "r", false, false, "unknown", []));
        Assert.Throws<ArgumentException>(() => Relation([new("id", 1, "text", true, IdentityKind: null!)]));
        Assert.Throws<ArgumentException>(() => Relation([new("id", 1, "text", true, GeneratedKind: "x")]));
        Assert.Throws<ArgumentException>(() => new SchemaRelation(Identity, "r", false, false, "d", [], constraints: [new("c", "x", null!)]));
        Assert.Throws<ArgumentException>(() => new SchemaRelation(Identity, "r", false, false, "d", [], policies: [new("p", "*", true, null!, null, null)]));
    }

    [Fact]
    public void Infinite_enumerables_stop_at_the_admission_limit()
    {
        var visits = 0;
        IEnumerable<SchemaColumn> Columns()
        {
            while (true) { visits++; yield return new("c" + visits, visits, "text", true); }
        }
        Assert.Throws<SchemaCaptureLimitException>(() => new SchemaRelation(Identity, "r", false, false, "d", Columns(),
            limits: new() { MaximumColumns = 2 }));
        Assert.Equal(3, visits);
    }

    [Fact]
    public void Aggregate_counts_and_metadata_bytes_are_bounded()
    {
        var a = Relation([new("a", 1, "text", true)]);
        var b = new SchemaRelation(new("app", "other"), "r", false, false, "d", [new("a", 1, "text", true)]);
        Assert.Throws<SchemaCaptureLimitException>(() => new SchemaSnapshot([a, b], new() { MaximumColumns = 1 }));
        Assert.Throws<SchemaCaptureLimitException>(() => new SchemaSnapshot([a], new() { MaximumMetadataBytes = 1 }));
        Assert.Throws<SchemaCaptureLimitException>(() => new SchemaRelation(Identity, "r", false, false, "d", [],
            definitionSql: new string('x', 1024), limits: new() { MaximumStringBytes = 100 }));
    }

    [Fact]
    public void Json_width_is_rejected_before_object_graph_materialization()
    {
        var snapshot = new SchemaSnapshot([Relation([new("a", 1, "text", true), new("b", 2, "text", true)])]);
        var bytes = SchemaSnapshotSerializer.Serialize(snapshot);
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaSnapshotSerializer.Deserialize(bytes, new() { MaximumColumns = 1 }));
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaSnapshotSerializer.Deserialize(bytes, new() { MaximumMetadataBytes = 1 }));
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaSnapshotSerializer.Deserialize(bytes, new() { MaximumStringBytes = 1 }));
    }

    [Fact]
    public void Invalid_json_members_produce_controlled_contract_errors()
    {
        var text = Encoding.UTF8.GetString(SchemaSnapshotSerializer.Serialize(new([Relation([new("id", 1, "text", true)])])));
        Assert.Throws<JsonException>(() => SchemaSnapshotSerializer.Deserialize(Encoding.UTF8.GetBytes(
            text.Replace("\"Columns\":[", "\"Columns\":[null,", StringComparison.Ordinal))));
        Assert.Throws<JsonException>(() => SchemaSnapshotSerializer.Deserialize(Encoding.UTF8.GetBytes(
            text.Replace("\"Kind\":\"r\"", "\"Kind\":\"bad\"", StringComparison.Ordinal))));
        Assert.Throws<JsonException>(() => SchemaSnapshotSerializer.Deserialize(Encoding.UTF8.GetBytes(
            text.Replace("\"IdentityKind\":\"\"", "\"IdentityKind\":null", StringComparison.Ordinal))));
    }

    [Fact]
    public void Serialization_enforces_exact_output_bound_without_rejecting_writer_growth_hints()
    {
        var snapshot = new SchemaSnapshot([Relation([new("id", 1, "text", true)])]);
        var bytes = SchemaSnapshotSerializer.Serialize(snapshot);
        Assert.Equal(bytes, SchemaSnapshotSerializer.Serialize(snapshot, bytes.Length));
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaSnapshotSerializer.Serialize(snapshot, bytes.Length - 1));
    }

    private static SchemaRelation Relation(IEnumerable<SchemaColumn> columns) => new(Identity, "r", false, false, "d", columns);
}

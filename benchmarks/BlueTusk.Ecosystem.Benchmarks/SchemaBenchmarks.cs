using BenchmarkDotNet.Attributes;
using BlueTusk.Schema;

namespace BlueTusk.Ecosystem.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class SchemaBenchmarks
{
    private SchemaSnapshot _before = null!;
    private SchemaSnapshot _after = null!;
    private byte[] _serialized = null!;

    [Params(100, 1000, 10_000)]
    public int RelationCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var relations = Enumerable.Range(0, RelationCount).Select(index => new SchemaRelation(
            new("app", "relation_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)), "r", true, false, "d",
            Enumerable.Range(1, 16).Select(ordinal => new SchemaColumn("column_" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ordinal, "text", true)))).ToArray();
        _before = new(relations);
        _after = new(relations.Select((relation, index) => index % 10 == 0
            ? new SchemaRelation(relation.Identity, "r", true, false, "d", relation.Columns.Append(new("added", 17, "text", true))) : relation));
        // A second catalogue capture creates independent models, even for unchanged rows.
        _after = SchemaSnapshotSerializer.Deserialize(SchemaSnapshotSerializer.Serialize(_after));
        _serialized = SchemaSnapshotSerializer.Serialize(_before);
    }

    [Benchmark]
    public SchemaComparison CompareContracts() => SchemaCompatibility.Compare(_before, _after);

    [Benchmark]
    public SchemaSnapshot DeserializeAndVerify() => SchemaSnapshotSerializer.Deserialize(_serialized);
}

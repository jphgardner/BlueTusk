using BenchmarkDotNet.Attributes;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class StructuredCodecBenchmarks
{
    private static readonly BlueTuskTypeId ArrayTypeId = new(90_001);
    private static readonly BlueTuskTypeId EnumTypeId = new(90_002);
    private static readonly BlueTuskTypeId RangeTypeId = new(90_003);
    private static readonly BlueTuskTypeId CompositeTypeId = new(90_004);
    private static readonly int[] Int32ArrayValue = [1, 2, 3, 4];

    private readonly byte[] _array =
        Convert.FromHexString(
            "00000001000000000000001700000004000000010000000400000001" +
            "000000040000000200000004000000030000000400000004");
    private readonly byte[] _enum = "in-progress"u8.ToArray();
    private readonly byte[] _range =
        Convert.FromHexString("02000000040000000A0000000400000014");
    private readonly byte[] _composite =
        Convert.FromHexString("0000000200000017000000040000002A000000190000000568656C6C6F");
    private readonly BlueTuskTypeRegistry _registry;
    private readonly BlueTuskParameter _arrayParameter;
    private readonly BlueTuskParameter _compositeParameter;
    private readonly IBlueTuskCodec _arrayCodec;
    private readonly IBlueTuskCodec _enumCodec;
    private readonly IBlueTuskCodec _rangeCodec;
    private readonly IBlueTuskCodec _compositeCodec;
    private readonly BlueTuskTypeDescriptor _arrayType;
    private readonly BlueTuskTypeDescriptor _enumType;
    private readonly BlueTuskTypeDescriptor _rangeType;
    private readonly BlueTuskTypeDescriptor _compositeType;

    public StructuredCodecBenchmarks()
    {
        _registry = CreateRegistry();
        (_arrayType, _arrayCodec) = Resolve(_registry, ArrayTypeId);
        (_enumType, _enumCodec) = Resolve(_registry, EnumTypeId);
        (_rangeType, _rangeCodec) = Resolve(_registry, RangeTypeId);
        (_compositeType, _compositeCodec) = Resolve(_registry, CompositeTypeId);
        _arrayParameter = new BlueTuskParameter(Int32ArrayValue)
        {
            PostgreSqlTypeOid = ArrayTypeId.Oid,
        };
        _compositeParameter = new BlueTuskParameter(
            new BlueTuskRecord(
            [
                new BlueTuskRecordField("answer", BlueTuskBuiltInTypes.Int4, 42),
                new BlueTuskRecordField("message", BlueTuskBuiltInTypes.Text, "hello"),
            ]))
        {
            PostgreSqlTypeOid = CompositeTypeId.Oid,
        };
    }

    [Benchmark(Baseline = true)]
    public int[] ReadInt32ArrayBinary()
    {
        var reader = new BlueTuskReader(_array);
        return (int[])_arrayCodec.Read(
            ref reader,
            BlueTuskDataFormat.Binary,
            _arrayType)!;
    }

    [Benchmark]
    public BlueTuskEnumValue ReadEnumBinary()
    {
        var reader = new BlueTuskReader(_enum);
        return (BlueTuskEnumValue)_enumCodec.Read(
            ref reader,
            BlueTuskDataFormat.Binary,
            _enumType)!;
    }

    [Benchmark]
    public BlueTuskRange<int> ReadInt32RangeBinary()
    {
        var reader = new BlueTuskReader(_range);
        return (BlueTuskRange<int>)_rangeCodec.Read(
            ref reader,
            BlueTuskDataFormat.Binary,
            _rangeType)!;
    }

    [Benchmark]
    public BlueTuskRecord ReadCompositeBinary()
    {
        var reader = new BlueTuskReader(_composite);
        return (BlueTuskRecord)_compositeCodec.Read(
            ref reader,
            BlueTuskDataFormat.Binary,
            _compositeType)!;
    }

    [Benchmark]
    public BlueTuskExtendedQueryParameter EncodeInt32ArrayParameter() =>
        BlueTuskParameterEncoder.Encode(_arrayParameter, _registry);

    [Benchmark]
    public BlueTuskExtendedQueryParameter EncodeCompositeParameter() =>
        BlueTuskParameterEncoder.Encode(_compositeParameter, _registry);

    private static BlueTuskTypeRegistry CreateRegistry() =>
        BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = BlueTuskBuiltInTypes.Int4.Id,
                Schema = "pg_catalog",
                Name = "int4",
                PostgreSqlKind = 'b',
                PostgreSqlCategory = 'N',
                ArrayType = ArrayTypeId,
            },
            new BlueTuskCatalogueType
            {
                Id = ArrayTypeId,
                Schema = "pg_catalog",
                Name = "_int4",
                PostgreSqlKind = 'b',
                PostgreSqlCategory = 'A',
                ElementType = BlueTuskBuiltInTypes.Int4.Id,
            },
            new BlueTuskCatalogueType
            {
                Id = EnumTypeId,
                Schema = "benchmark",
                Name = "status",
                PostgreSqlKind = 'e',
                PostgreSqlCategory = 'E',
                EnumLabels = ["pending", "in-progress", "complete"],
            },
            new BlueTuskCatalogueType
            {
                Id = RangeTypeId,
                Schema = "benchmark",
                Name = "int_span",
                PostgreSqlKind = 'r',
                PostgreSqlCategory = 'R',
                RangeSubtype = BlueTuskBuiltInTypes.Int4.Id,
                RangeType = RangeTypeId,
            },
            new BlueTuskCatalogueType
            {
                Id = CompositeTypeId,
                Schema = "benchmark",
                Name = "sample",
                PostgreSqlKind = 'c',
                PostgreSqlCategory = 'C',
                CompositeFields =
                [
                    new BlueTuskCompositeField
                    {
                        Position = 1,
                        Name = "answer",
                        Type = BlueTuskBuiltInTypes.Int4.Id,
                    },
                    new BlueTuskCompositeField
                    {
                        Position = 2,
                        Name = "message",
                        Type = BlueTuskBuiltInTypes.Text.Id,
                    },
                ],
            },
        ]);

    private static (BlueTuskTypeDescriptor Type, IBlueTuskCodec Codec) Resolve(
        BlueTuskTypeRegistry registry,
        BlueTuskTypeId id)
    {
        if (!registry.TryGetType(id, out var type) ||
            !registry.TryGetCodec(id, out var codec))
        {
            throw new InvalidOperationException($"Benchmark type OID {id} was not composed.");
        }

        return (type!, codec!);
    }
}

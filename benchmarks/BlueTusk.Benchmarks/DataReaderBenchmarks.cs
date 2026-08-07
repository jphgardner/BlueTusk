using System.Buffers.Binary;
using System.Data;
using BenchmarkDotNet.Attributes;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class DataReaderBenchmarks
{
    private readonly BlueTuskTypeRegistry _types = BlueTuskBuiltInTypes.CreateRegistry();
    private readonly BlueTuskQueryResult _thousandInt32Rows;
    private readonly BlueTuskQueryResult _largeBytea;
    private readonly BlueTuskQueryResult _largeText;
    private readonly byte[] _streamBuffer = new byte[8192];
    private readonly char[] _textBuffer = new char[4096];

    public DataReaderBenchmarks()
    {
        var intField = new BlueTuskFieldDescription("value", 0, 0, 23, 4, -1, 1);
        _thousandInt32Rows = new BlueTuskQueryResult(
        [
            new BlueTuskResultSet(
                [intField],
                Enumerable.Range(0, 1000)
                    .Select(
                        static value =>
                        {
                            var bytes = new byte[sizeof(int)];
                            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
                            return new BlueTuskDataRow([bytes]);
                        })
                    .ToArray(),
                "SELECT 1000"),
        ]);
        _largeBytea = SingleValueResult(
            new BlueTuskFieldDescription("payload", 0, 0, 17, -1, -1, 1),
            new byte[1024 * 1024]);
        _largeText = SingleValueResult(
            new BlueTuskFieldDescription("payload", 0, 0, 25, -1, -1, 1),
            Enumerable.Repeat((byte)'x', 1024 * 1024).ToArray());
    }

    [Benchmark(Baseline = true)]
    public int ReadThousandTypedInt32Rows()
    {
        using var reader = CreateReader(_thousandInt32Rows);
        var sum = 0;
        while (reader.Read())
        {
            sum += reader.GetInt32(0);
        }

        return sum;
    }

    [Benchmark]
    public int ReadThousandGenericInt32Rows()
    {
        using var reader = CreateReader(_thousandInt32Rows);
        var sum = 0;
        while (reader.Read())
        {
            sum += reader.GetFieldValue<int>(0);
        }

        return sum;
    }

    [Benchmark]
    public int ReadOneMegabyteByteaStream()
    {
        using var reader = CreateReader(_largeBytea, CommandBehavior.SequentialAccess);
        _ = reader.Read();
        using var stream = reader.GetStream(0);
        var total = 0;
        int read;
        while ((read = stream.Read(_streamBuffer)) != 0)
        {
            total += read;
        }

        return total;
    }

    [Benchmark]
    public int ReadOneMegabyteTextReader()
    {
        using var reader = CreateReader(_largeText);
        _ = reader.Read();
        using var text = reader.GetTextReader(0);
        var total = 0;
        int read;
        while ((read = text.Read(_textBuffer)) != 0)
        {
            total += read;
        }

        return total;
    }

    private BlueTuskDataReader CreateReader(
        BlueTuskQueryResult result,
        CommandBehavior behavior = CommandBehavior.Default) =>
        new(result, connectionToClose: null, _types, behavior);

    private static BlueTuskQueryResult SingleValueResult(
        BlueTuskFieldDescription field,
        ReadOnlyMemory<byte> value) =>
        new(
        [
            new BlueTuskResultSet(
                [field],
                [new BlueTuskDataRow([value])],
                "SELECT 1"),
        ]);
}

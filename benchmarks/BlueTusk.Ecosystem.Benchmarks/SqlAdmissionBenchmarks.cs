using BenchmarkDotNet.Attributes;
using BlueTusk.Sql;

namespace BlueTusk.Ecosystem.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class SqlAdmissionBenchmarks
{
    private string _sql = null!;

    [Params(128, 4096, 65_536)]
    public int SqlBytes { get; set; }

    [GlobalSetup]
    public void Setup() => _sql = "SELECT $literal$" + new string('x', SqlBytes) + "$literal$ AS value /* nested /* ; */ */";

    [Benchmark]
    public string AdmitQuotedQuery() => PostgreSqlStatementGuard.AdmitReadQuery(_sql);
}

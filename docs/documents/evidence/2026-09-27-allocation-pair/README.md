# Retained-binary Documents allocation comparison

BenchmarkDotNet 0.15.8 ShortRun, in-process emit, one launch, three warmups and three measured iterations. The six cases used source-generated JSON and staged eight documents without database I/O. The original and optimized executables were run from separate retained dependency directories; all 34 dependency DLL hashes in each directory were rechecked after measurement. No provider/project restore or compilation occurred in this quiet repository CPU phase. Windows Ryzen 7 5800X, .NET 10.0.12, x64 RyuJIT. Other operating-system activity was not controlled.

| Benchmark | Source payload | Baseline B/op | Optimized B/op | Saved B/op | Saved |
| --- | ---: | ---: | ---: | ---: | ---: |
| StageEightDocuments | 1 KiB | 40,400 | 30,992 | 9,408 | 23.29% |
| StageEightDocuments | 64 KiB | 3,418,218 | 2,850,288 | 567,930 | 16.61% |
| StageEightDocuments | 1 MiB | 54,273,630 | 45,228,975 | 9,044,655 | 16.66% |

The unchanged SourceGeneratedSerialize control allocated exactly 1,176 / 71,032 / 1,130,740 B/op for 1 KiB / 64 KiB / 1 MiB in both runs. The implementation removes the completed UTF8 buffer copy in DocumentSession.Serialize, parses the stream-owned written memory while its stream is alive, then creates an owned string. The native smoke and all 30 Documents unit/live PostgreSQL tests passed after the change. Type bounds, invalid root rejection, and mixed large/small owned JSON roundtrips are exercised.

Staging means do not establish a speed gain. At 1 MiB, baseline mean 28.200 ms (99.9% CI 20.816–35.585) and optimized mean 26.545 ms (CI 23.845–29.245) overlap. ShortRun has only three measured iterations per case. The same applies to the 1 KiB and 64 KiB mean intervals. None of these cases saves to PostgreSQL, predicts database throughput, resolves long write tails, or addresses physical TOAST growth.

The old Documents DLL SHA-256 was `592C14C8BC951E230DB15E6A4BDBF20A651DC8148B9F5DF4548984255BFF5075`; the optimized DLL was `8AAA7912591EE0CDA86BC23A2E8ACCA296D9DB799F58B3F8A74A201D8BA3C31E`. Data also changed between the retained old and optimized dependency sets due a separate private routing correction: old `6987820540B53F8DBEA9D15B32826B1F135BEADB686F385A889365B34D79EF22`, optimized `4F4C906F0B55086E9A4DCE3050F55E0702A02715F9A2165498BAFD25AEA0DAF1`. Its construction is in benchmark setup rather than timed staging; the unchanged serialization control is identical. The code change and paired allocation delta are consistent with removing eight buffer copies; this is not an isolated Documents-only dependency build.

The raw [binary bindings](binary-bindings.json), [comparison](comparison.json), baseline [BenchmarkDotNet JSON](baseline/benchmarkdotnet-brief.json) and [console log](baseline/console.log), and optimized [BenchmarkDotNet JSON](optimized/benchmarkdotnet-brief.json) and [console log](optimized/console.log) are retained here. Exact runnable binaries remain in ignored local artifacts under `artifacts/documents-load/final-pg18-600s/baseline-bin` and `artifacts/documents-load/allocation-pair/optimized-bin`; the raw hashes bind these reports to those executables.

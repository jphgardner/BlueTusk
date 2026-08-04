# NativeAOT and trimming

The provider core supports trimmed and NativeAOT applications across
`BlueTusk.Transport`, `BlueTusk.Protocol`, `BlueTusk.Security`,
`BlueTusk.TypeSystem`, `BlueTusk.Client`, `BlueTusk.Diagnostics`, and
`BlueTusk.Data`. `BlueTusk.Extensions.Abstractions`, a required Data dependency,
is covered by the same gate.

The repository verifies this support by publishing and executing two
self-contained offline applications:

- `BlueTusk.TrimSmoke` uses full trimming.
- `BlueTusk.NativeAotSmoke` uses NativeAOT.

Both applications exercise endpoint and protocol construction, SCRAM,
connection-string parsing, diagnostics, data-source and command construction,
built-in arrays, a source-generated composite, and the
reflection-based composite fallback. The smoke does not contact PostgreSQL, so
it is deterministic and does not need credentials.

Run the complete publish and measurement gate for the current platform:

```powershell
dotnet restore tests/BlueTusk.TrimSmoke/BlueTusk.TrimSmoke.csproj -r win-x64
dotnet restore tests/BlueTusk.NativeAotSmoke/BlueTusk.NativeAotSmoke.csproj -r win-x64
./eng/verify-provider-core-publish.ps1 -RuntimeIdentifier win-x64 -NoRestore
```

The gate records total output size, deployable size (excluding optional PDB and
XML documentation files), executable size, cold process wall-clock, and
second-pass managed allocation in
`artifacts/provider-core-smoke/<rid>/report.json`. The checked-in budgets are
regression limits, not claims about application startup or allocation under a
real database workload. CI publishes and executes `win-x64` and `linux-x64`
variants and archives each report.

The first checked-in Windows x64 observation is:

| Mode | Deployable bytes | Cold wall-clock | Second-pass managed allocation |
| --- | ---: | ---: | ---: |
| Full trim | 21,993,850 | 248.994 ms | 327,144 B |
| NativeAOT | 5,783,552 | 18.327 ms | 343,392 B |

These values come from the offline smoke on .NET 10.0.9 and Windows
10.0.26200. They establish regression evidence, not a comparison with Npgsql or
a real connection. Both are below their checked-in budgets, so this slice does
not introduce a separate slim builder. That decision remains evidence-driven
and can be revisited after representative application measurements.

## Composite and enum mappings

Prefer the `BlueTusk.SourceGeneration` composite generator in NativeAOT
applications. It produces direct member access and avoids reflection during
normal encoding and decoding:

```csharp
[BlueTuskComposite("app", "address")]
internal sealed partial record Address(int HouseNumber, string Street);

var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .ConfigureTypes(Address.RegisterCodec)
    .Build();
```

`MapComposite<T>` remains available for statically known public constructors,
properties, and fields. Its generic annotations preserve those members during
trimming. Source generation is still preferred because it gives compile-time
mapping diagnostics and removes reflection from the hot path.

`MapEnum<TEnum>` preserves the enum fields needed by its label mapping.
Dynamically discovering a CLR type by name and then constructing a closed
generic mapping is not supported in NativeAOT; register the concrete type
directly in application code.

## Array boundary

NativeAOT supports one-dimensional PostgreSQL arrays with the standard lower
bound of 1. The provider creates these arrays through statically rooted generic
code.

Multidimensional arrays and arrays with non-standard lower bounds require
runtime array construction and are therefore supported only by JIT deployments.
A NativeAOT application receives an explicit `NotSupportedException` instead
of a silent shape change. JIT deployments retain ranks one through six and
non-standard lower bounds.

Custom element codecs used by an array must derive from `BlueTuskCodec<T>` in a
NativeAOT application. A codec that implements `IBlueTuskCodec` directly does
not provide a statically rooted array factory and is rejected with an
actionable `NotSupportedException`. The direct implementation remains
supported in JIT deployments.

`DbDataReader.GetFieldValue<T>` returns a codec-native array without conversion.
One-dimensional conversions to `decimal[]`, `TimeOnly[]`, and `TimeSpan[]` are
also statically supported. An arbitrary conversion to an array type selected at
runtime requires a JIT deployment.

## Range boundary

The built-in `int4`, `int8`, `numeric`, `date`, `timestamp`, and `timestamptz`
ranges, multiranges, and their arrays have statically rooted codecs. A custom
range or a range whose subtype is itself a range or multirange requires a
closed generic codec selected from the runtime catalogue. That case remains
available in a JIT deployment. NativeAOT installs an unsupported codec that
fails explicitly when materialisation is attempted; applications can instead
register their own statically implemented codec.

## Scope

The publish gate covers the provider core only. EF Core, extensions, Streams,
Sync, Live, the Control Plane, and Continuous Graph are not currently declared
NativeAOT-compatible by this gate. Applications must evaluate those packages
separately and must not infer whole-application NativeAOT support from the
provider-core result.

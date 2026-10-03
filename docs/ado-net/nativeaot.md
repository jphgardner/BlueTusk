# NativeAOT and trimming

This page helps you publish a trimmed or NativeAOT application that uses
`BlueTusk.Data`, and lists the few features that need a JIT runtime.

## What is supported

The provider core supports full trimming and NativeAOT. That covers
`BlueTusk.Data` and the packages it depends on: `BlueTusk.Client`,
`BlueTusk.Protocol`, `BlueTusk.Transport`, `BlueTusk.Security`,
`BlueTusk.TypeSystem`, `BlueTusk.Diagnostics` and
`BlueTusk.Extensions.Abstractions`. Connection strings, SCRAM authentication,
data sources, commands, built-in types and one-dimensional arrays all work.

Every build of the provider is published and run as a trimmed app and as a
NativeAOT app on Windows x64 and Linux x64. Those smoke apps do not contact a
server, so test your own app against PostgreSQL after publishing.

## Set up your project

Turn on NativeAOT and add the composite source generator:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

```powershell
dotnet add package BlueTusk.Data
dotnet add package BlueTusk.SourceGeneration
```

`PublishAot` also turns on the trimming and AOT analyzers at build time, so
unsupported patterns show up as build warnings.

## Composite and enum mappings

Prefer the `BlueTusk.SourceGeneration` composite generator in NativeAOT
applications. It produces direct member access and avoids reflection during
normal encoding and decoding:

```csharp
using BlueTusk.Data;
using BlueTusk.TypeSystem;

var builder = new BlueTuskDataSourceBuilder(connectionString);
Address.RegisterCodec(builder.Types);
await using var dataSource = builder.Build();

[BlueTuskComposite("app", "address")]
internal sealed partial record Address(int HouseNumber, string Street);
```

The generator adds a static `RegisterCodec` method to every `partial` type
marked with `[BlueTuskComposite]`. Members match composite fields by snake_case
name (`HouseNumber` matches `house_number`).

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

## Other BlueTusk packages

Only the provider core is NativeAOT-compatible. EF Core, extensions, Streams,
Sync, Live, the Control Plane and Continuous Graph are not. Check each package
your app uses before you publish with NativeAOT.

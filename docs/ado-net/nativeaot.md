# NativeAOT and trimming

Publish your app with a runtime identifier and verify it against the
PostgreSQL version you deploy:

~~~sh
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
~~~

For trimmed JIT output use `-p:PublishTrimmed=true` instead. Use
source-generated mappings when the native compiler needs static knowledge of
PostgreSQL enum or composite values. Review trimming analyzer warnings and
exercise all supported data paths in the published executable.
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

## Integration scope

This guidance applies to the core data provider. Check trimming and NativeAOT
support for each optional package you add, including EF Core integrations,
type codecs, and real-time hosts.

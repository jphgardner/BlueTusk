# BlueTusk.SourceGeneration

`BlueTusk.SourceGeneration` generates reflection-free member access and CLR
construction for PostgreSQL composite codecs.

Reference the package as an analyzer, mark a top-level CLR class, record, or
struct as `partial`, and declare its catalogue identity:

```xml
<PackageReference Include="BlueTusk.SourceGeneration"
                  PrivateAssets="all"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

```csharp
using BlueTusk.TypeSystem;

[BlueTuskComposite("app", "address")]
public sealed partial record Address(int HouseNumber, string Street);

Address.RegisterCodec(dataSourceBuilder.Types);
```

The generator applies the same snake-case and `[BlueTuskName]` conventions as
the runtime mapper. It emits an `IBlueTuskCodec<T>` backed by generated member
delegates and a generated construction delegate; catalogue discovery still
binds field OIDs and nested codecs at data-source initialization time. This
keeps PostgreSQL metadata authoritative while removing reflection from CLR
member discovery and object construction.

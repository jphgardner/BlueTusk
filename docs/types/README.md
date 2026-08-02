# Core type mappings

BlueTusk registers PostgreSQL scalar types by catalogue OID. Simple queries decode the server's text format. Extended queries prefer binary fields and transparently retry with text outside a transaction when a selected PostgreSQL type has no binary output function. Inside an explicit transaction they request text fields up front, avoiding a failed Bind that would abort the transaction.

| PostgreSQL type | OID | Default CLR value | Text | Binary |
| --- | ---: | --- | :---: | :---: |
| `bool` | 16 | `bool` | yes | yes |
| `bytea` | 17 | `byte[]` | yes | yes |
| `"char"` | 18 | `BlueTuskInternalChar` | yes | yes |
| `name` | 19 | `string` | yes | yes |
| `int8` | 20 | `long` | yes | yes |
| `int2` | 21 | `short` | yes | yes |
| `int4` | 23 | `int` | yes | yes |
| `text` | 25 | `string` | yes | yes |
| `oid` | 26 | `uint` | yes | yes |
| `json` | 114 | `string` | yes | yes |
| `xml` | 142 | `string` | yes | yes |
| `float4` | 700 | `float` | yes | yes |
| `float8` | 701 | `double` | yes | yes |
| `bpchar` | 1042 | `string` | yes | yes |
| `varchar` | 1043 | `string` | yes | yes |
| `date` | 1082 | `DateOnly` | yes | yes |
| `time` | 1083 | `TimeSpan` | yes | yes |
| `timestamp` | 1114 | `DateTime` | yes | yes |
| `timestamptz` | 1184 | `DateTimeOffset` | yes | yes |
| `numeric` | 1700 | `BlueTuskNumeric` | yes | yes |
| `uuid` | 2950 | `Guid` | yes | yes |
| `jsonb` | 3802 | `string` | yes | yes |

## Numeric values

`BlueTuskNumeric` preserves PostgreSQL arbitrary precision as a `BigInteger` unscaled value and a scale up to 16,383. It also represents `NaN`, positive infinity, and negative infinity. `GetDecimal` and `GetFieldValue<decimal>` perform a checked conversion and fail when the value is special or outside `decimal`'s range.

Use `BlueTuskParameter<BlueTuskNumeric>` to send values that cannot fit in `decimal`:

```csharp
var value = BlueTuskNumeric.Parse("123456789012345678901234567890.123456789");
command.Parameters.Add(new BlueTuskParameter<BlueTuskNumeric>(value));
```

## Temporal values

PostgreSQL `date` infinity maps to `DateOnly.MinValue` and `DateOnly.MaxValue`. `timestamp` infinity maps to `DateTime.MinValue` and `DateTime.MaxValue`; `timestamptz` uses the corresponding `DateTimeOffset` values and normalizes finite results to UTC.

PostgreSQL `time` can represent `24:00:00`, so its default CLR type is `TimeSpan`. `GetFieldValue<TimeOnly>` is available for values before 24:00. Binary temporal values preserve PostgreSQL's microsecond precision.

`BlueTuskInterval` represents PostgreSQL finite intervals and the positive/negative infinity values supported by PostgreSQL 17 and later. PostgreSQL 15–16 reject interval infinity at the server boundary.

## Binary and streaming behavior

UUID binary values use PostgreSQL network byte order, JSONB validates its version byte, and `bytea` accepts binary, hexadecimal text, and legacy escape text. UTF-8 decoding is strict so malformed server text is rejected rather than silently replaced.

The default data reader buffers complete result sets for random field access. A reader created with `CommandBehavior.SequentialAccess` instead uses a bounded named portal: rows remain on the PostgreSQL connection until requested, fields must be visited in ordinal order, binary `bytea` is exposed directly through `GetStream`, and text, JSON, and JSONB are decoded incrementally through `GetTextReader`. Materializing a scalar value buffers only that field.

An unregistered OID is returned as `BlueTuskUnknownValue`, preserving its format and raw bytes.

## Catalogue-discovered structured types

Each data source loads PostgreSQL type relationships from the system catalogues. Arrays, domains, enums, composites, records, ranges, and multiranges are composed from the codecs of their contained types in both text and binary formats. Runtime codecs can be registered by schema-qualified catalogue name, while `MapEnum<TEnum>` and `MapComposite<T>` provide typed mappings for user-defined enums and composites.

Applications that want compile-time composite member discovery can reference
`BlueTusk.SourceGeneration` as an analyzer and annotate a top-level partial CLR type:

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

Address.RegisterBlueTuskCodec(dataSourceBuilder.Types);
```

The generator follows the runtime mapper's snake-case and `[BlueTuskName]`
conventions. It emits typed member getters and CLR construction, while data-source
initialization still resolves the PostgreSQL fields, OIDs, nested codecs, and array
codec from the live catalogue. The generated CLR members must exactly cover the
catalogue composite. `MapComposite<T>` remains the reflection-based fallback for
types that do not opt in. See the
[source-generator package guide](../../src/BlueTusk.SourceGeneration/README.md)
for supported CLR shapes and registration details.

Set `BlueTuskParameter.PostgreSqlTypeName` when a parameter—especially a null value—must select one of those catalogue-discovered types. Names are parsed using PostgreSQL identifier rules: unquoted identifiers fold to lowercase, quoted identifiers preserve case and may contain dots, and a trailing `[]` selects the discovered array type.

`BlueTuskRange<T>` keeps empty ranges distinct from ranges with one or two unbounded sides. Construct finite bounds with `BlueTuskRangeBound.Inclusive(value)` or `BlueTuskRangeBound.Exclusive(value)`, and use `BlueTuskRangeBound.Unbounded<T>()` for an infinite side:

```csharp
var finite = new BlueTuskRange<int>(1, 10); // [1,10)
var upperBounded = new BlueTuskRange<int>(
    BlueTuskRangeBound.Unbounded<int>(),
    BlueTuskRangeBound.Inclusive(10));
var empty = BlueTuskRange.Empty<int>();
```

`BlueTuskMultirange<T>` is an immutable ordered collection of `BlueTuskRange<T>` values. Range and multirange arrays are discovered and composed automatically, and all four forms participate in parameter type inference when the CLR mapping is unique.

## Transaction catalogue values

PostgreSQL's unsigned transaction identifiers have dedicated CLR values so their complete wire ranges are preserved:

| PostgreSQL type | CLR value |
| --- | --- |
| `xid` | `BlueTuskTransactionId` |
| `cid` | `BlueTuskCommandId` |
| `xid8` | `BlueTuskFullTransactionId` |
| `pg_snapshot` | `BlueTuskTransactionSnapshot` |
| `txid_snapshot` | `BlueTuskTransactionSnapshot` |

`BlueTuskTransactionSnapshot` validates PostgreSQL's ordered half-open snapshot invariants and defensively copies the in-progress transaction IDs. Modern `pg_snapshot` is the default parameter inference target. The deprecated `txid_snapshot` remains readable and writable by specifying its PostgreSQL OID explicitly. Catalogue-discovered arrays of all five types are composed automatically.

## Object identifiers and catalogue vectors

PostgreSQL 19's unsigned 64-bit `oid8` maps to
`BlueTuskObjectIdentifier64`, preserving the complete `0` through
`UInt64.MaxValue` range in text and big-endian binary formats. Its array type
is catalogue-composed like other built-ins. Earlier PostgreSQL releases do not
advertise this type, so the runtime catalogue does not register it there.

The PostgreSQL `reg*` aliases use symbolic names in text and unsigned four-byte OIDs in binary. BlueTusk provides a distinct CLR wrapper for each alias so parameter inference remains unambiguous:

| PostgreSQL type | CLR value |
| --- | --- |
| `regproc` | `BlueTuskRegProc` |
| `regprocedure` | `BlueTuskRegProcedure` |
| `regoper` | `BlueTuskRegOper` |
| `regoperator` | `BlueTuskRegOperator` |
| `regclass` | `BlueTuskRegClass` |
| `regtype` | `BlueTuskRegType` |
| `regconfig` | `BlueTuskRegConfig` |
| `regdictionary` | `BlueTuskRegDictionary` |
| `regnamespace` | `BlueTuskRegNamespace` |
| `regrole` | `BlueTuskRegRole` |
| `regcollation` | `BlueTuskRegCollation` |
| `regdatabase` (PostgreSQL 19+) | `BlueTuskRegDatabase` |

Construct an alias from either form. The returned `Identifier` preserves whether a text result was symbolic or numeric:

```csharp
var relationByName = new BlueTuskRegClass("public.orders");
var relationByOid = new BlueTuskRegClass(16_384);
```

Symbolic values are sent as text so PostgreSQL resolves them using its normal namespace and search-path rules. Numeric values use binary. The same value-sensitive choice applies to catalogue-composed arrays.

The built-in type coverage acceptance test queries `pg_catalog.pg_type` on
every supported server and requires a codec for every queryable base, range,
and multirange type. This keeps new PostgreSQL built-ins visible as an
executable compatibility failure rather than silently treating them as an
unknown type. See PostgreSQL 19's
[object identifier type documentation](https://www.postgresql.org/docs/19/datatype-oid.html)
for `oid8` and `regdatabase` semantics.

`int2vector` maps to the immutable `BlueTuskInt16Vector`; `oidvector` maps to `BlueTuskObjectIdentifierVector`. Their codecs enforce PostgreSQL's one-dimensional, zero-based, null-free binary shape, including full unsigned OID values. PostgreSQL does not accept an empty vector through its binary receive function, so BlueTusk automatically uses text for an empty vector or an array containing one.

Runtime codecs that have the same value-dependent requirement can implement `IBlueTuskWriteFormatSelector`. Its `DefaultWriteFormat` also controls empty composed arrays, whose elements cannot provide a value-specific choice. Otherwise registered codec parameters continue to prefer binary.

## JSONPath and text-like catalogue values

| PostgreSQL type | CLR value | Notes |
| --- | --- | --- |
| `"char"` | `BlueTuskInternalChar` | One raw byte; distinct from SQL `char(n)` |
| `refcursor` | `BlueTuskRefCursor` | PostgreSQL portal name |
| `pg_node_tree` | `BlueTuskNodeTree` | Opaque, decode-only expression tree |
| `jsonpath` | `BlueTuskJsonPath` | SQL/JSON path expression |

`BlueTuskInternalChar` preserves all 256 values. Its text codec implements PostgreSQL's empty representation for zero, single-byte ASCII representation, and backslash-plus-octal representation for values with the high bit set.

`BlueTuskJsonPath` deliberately preserves the expression as text and lets PostgreSQL parse, validate, and normalize it. Its binary codec validates the PostgreSQL JSONPath wire-version byte. `refcursor` values and arrays use their own CLR type so they do not collide with ordinary `text` inference.

PostgreSQL exposes `pg_node_tree` through system catalogues but rejects input values of that type. BlueTusk therefore decodes its text and binary forms into `BlueTuskNodeTree`, while attempts to encode one fail locally with `NotSupportedException`.

## Text-only and opaque catalogue values

| PostgreSQL type | CLR value | Behavior |
| --- | --- | --- |
| `aclitem` | `BlueTuskAccessControlItem` | Text-only read/write; PostgreSQL validates ACL syntax |
| `gtsvector` | `BlueTuskGistTextSearchVector` | Text-only, decode-only GiST signature |
| `pg_ndistinct` | `BlueTuskNDistinctStatistics` | Decode-only opaque statistics payload |
| `pg_dependencies` | `BlueTuskDependencyStatistics` | Decode-only opaque statistics payload |
| `pg_mcv_list` | `BlueTuskMostCommonValueStatistics` | Decode-only opaque statistics payload |
| `pg_brin_bloom_summary` | `BlueTuskBrinBloomSummary` | Decode-only opaque BRIN summary |
| `pg_brin_minmax_multi_summary` | `BlueTuskBrinMinMaxMultiSummary` | Decode-only opaque BRIN summary |

`aclitem` has no binary input or output functions. Its codec and catalogue-composed arrays therefore always use text, including empty arrays. `gtsvector` likewise has no binary output and PostgreSQL rejects input values.

The statistics and BRIN values preserve their exact field format and bytes through distinct immutable CLR types. Their internal representation is deliberately not interpreted: PostgreSQL owns that version-specific encoding and rejects client input even where a receive function appears in the catalogue. BlueTusk consequently exposes these values for lossless inspection and rejects attempts to encode them.

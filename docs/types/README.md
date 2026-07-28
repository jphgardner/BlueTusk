# Core type mappings

BlueTusk registers PostgreSQL scalar types by catalogue OID. Simple queries decode the server's text format; extended queries request and decode binary fields.

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

## Binary and streaming behavior

UUID binary values use PostgreSQL network byte order, JSONB validates its version byte, and `bytea` accepts binary, hexadecimal text, and legacy escape text. UTF-8 decoding is strict so malformed server text is rejected rather than silently replaced.

The current data reader buffers complete result sets. `GetStream` returns a read-only stream over a buffered `bytea`; `GetTextReader` exposes buffered text, JSON, and JSONB strings. Network-backed sequential access remains scheduled for 0.1.0.

An unregistered OID is returned as `BlueTuskUnknownValue`, preserving its format and raw bytes.

## Catalogue-discovered structured types

Each data source loads PostgreSQL type relationships from the system catalogues. Arrays, domains, enums, composites, records, ranges, and multiranges are composed from the codecs of their contained types in both text and binary formats. Runtime codecs can be registered by schema-qualified catalogue name, while `MapEnum<TEnum>` and `MapComposite<T>` provide typed mappings for user-defined enums and composites.

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

Construct an alias from either form. The returned `Identifier` preserves whether a text result was symbolic or numeric:

```csharp
var relationByName = new BlueTuskRegClass("public.orders");
var relationByOid = new BlueTuskRegClass(16_384);
```

Symbolic values are sent as text so PostgreSQL resolves them using its normal namespace and search-path rules. Numeric values use binary. The same value-sensitive choice applies to catalogue-composed arrays.

`int2vector` maps to the immutable `BlueTuskInt16Vector`; `oidvector` maps to `BlueTuskObjectIdentifierVector`. Their codecs enforce PostgreSQL's one-dimensional, zero-based, null-free binary shape, including full unsigned OID values. PostgreSQL does not accept an empty vector through its binary receive function, so BlueTusk automatically uses text for an empty vector or an array containing one.

Runtime codecs that have the same value-dependent requirement can implement `IBlueTuskWriteFormatSelector`. Otherwise registered codec parameters continue to prefer binary.

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

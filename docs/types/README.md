# Core type mappings

Version 0.0.6 registers the following PostgreSQL scalar types by catalogue OID. Simple queries decode the server's text format; extended queries request and decode binary fields.

| PostgreSQL type | OID | Default CLR value | Text | Binary |
| --- | ---: | --- | :---: | :---: |
| `bool` | 16 | `bool` | yes | yes |
| `bytea` | 17 | `byte[]` | yes | yes |
| `char` | 18 | `string` | yes | yes |
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

An unregistered OID is returned as `BlueTuskUnknownValue`, preserving its format and raw bytes. Runtime codec registration and catalogue-discovered structured types are scheduled for 0.0.7.

# Protocol capture format

BlueTusk protocol captures use the `.btpc` extension. The format is a streamable, big-endian container intended for conformance debugging and packet-laboratory fixtures; it is not a general network-capture replacement.

## File header

| Offset | Size | Value |
| ---: | ---: | --- |
| 0 | 8 | ASCII magic `BTPCAP\r\n` |
| 8 | 2 | Format version (`1`) |
| 10 | 2 | File-header length (`24`) |
| 12 | 4 | File attributes (zero in version 1) |
| 16 | 8 | Creation time as Unix milliseconds |

## Record

Each file-header is followed by zero or more records.

| Offset | Size | Value |
| ---: | ---: | --- |
| 0 | 1 | Direction: frontend (`0`) or backend (`1`) |
| 1 | 1 | Attributes: redacted (`1`) and encrypted (`2`) |
| 2 | 2 | Record-header length (`16`) |
| 4 | 8 | Microseconds elapsed since capture start |
| 12 | 4 | Payload length |
| 16 | variable | Captured payload bytes |

Readers reject unknown versions, unsupported attributes, invalid directions, truncated data, negative lengths, and payloads beyond a configurable limit before allocating the payload buffer. The default limit is 64 MiB.

Capture producers must mark or remove authentication secrets, tokens, password messages, and other sensitive payloads. The inspector does not display payload bytes by default and never displays a record marked redacted.

```powershell
dotnet run --project tooling/BlueTusk.ProtocolInspector -- capture.btpc
dotnet run --project tooling/BlueTusk.ProtocolInspector -- capture.btpc --hex
```

# Provider client topology for Core 1.2

Core 1.2 measures both clients on the Windows host and the native Linux client
in the Linux runner. The client runtime determines the variant. Every variant
uses the same digest-pinned PostgreSQL 18 Linux image.

| Environment | Client and connection |
| --- | --- |
| Windows, `windows` | Native Windows .NET client connects through the fixture's published loopback port. |
| Windows, `linux` | The same Release build runs in a pinned Linux .NET container on the Windows host and connects over the fixture's Docker network. |
| Linux, `linux` | Native Linux .NET client runs in the Linux runner and connects over the fixture's Docker network. |
| Both, `tls` | The native client negotiates TLS and verifies the fixture's certificate. |
| Both, `constrained-network` | The native client connects through the pinned Toxiproxy network profile. |

The Windows host has four variants and the Linux runner has three. The 16
features and three concurrency levels produce 336 Provider comparisons and
488 Core comparisons in total. Each Provider comparison measures BlueTusk and
Npgsql using the same runtime and network route. The Windows-native and Linux
container routes have separate comparison keys and their results are evaluated
independently.

[`resolve-core-performance-release.ps1`](resolve-core-performance-release.ps1)
applies this topology to the exact v1.2 candidate. Its contribution to the
contract is the per-environment Provider variant list. The 1.1 contract and
map remain available for verifying historical evidence.

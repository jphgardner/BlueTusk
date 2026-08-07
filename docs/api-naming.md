# Public API naming

BlueTusk names should read naturally after the caller has already selected a
BlueTusk package or entered a PostgreSQL-specific domain. Repeating the product
name in every fluent member obscures the operation without adding useful
context.

## V1 rule

- Keep `BlueTusk` on primary product types where ownership or collision
  avoidance matters, such as `BlueTuskDataSource`, `BlueTuskConnection`, and
  `IBlueTuskCodec`.
- Keep the brand on neutral framework entry points that select or compose a
  product, such as `UseBlueTusk`, `AddBlueTuskStreams`,
  `MapBlueTuskDashboard`, and Aspire `WithBlueTusk...` methods.
- Keep it on explicit conversions whose result is a branded value type, such as
  `ToBlueTuskGeometry`.
- Do not repeat it inside an operation after the receiver and namespace already
  establish the domain.

Examples from the V1 cleanup:

| Preview name | V1 name |
| --- | --- |
| `HasBlueTuskExtension` | `HasExtension` |
| `CreateBlueTuskPublication` | `CreatePublication` |
| `UseBlueTuskIndexMethod` | `UseIndexMethod` |
| `IsBlueTuskConcurrent` | `IsConcurrent` |
| `EnsureBlueTuskPgVector` | `EnsurePgVector` |
| `RegisterBlueTuskCodec` | `RegisterCodec` |
| `GetBlueTuskTriggers` | `GetTriggerDefinitions` |

Migration-operation types follow the same rule inside the
`BlueTusk.EntityFrameworkCore.Migrations.Operations` namespace:
`CreateExtensionOperation`, not `CreateBlueTuskExtensionOperation`.

This is an intentional pre-V1 source break. The redundant preview names are not
retained as obsolete aliases because doing so would permanently pollute
IntelliSense and the V1 compatibility floor. Normal compiler errors point
directly to the natural replacement, and samples, tests, generated code, and
documentation use only the V1 names.

`PublicApiNamingTests` scans every compiler-enforced API baseline. A new public
method with `BlueTusk` embedded in its operation name fails the normal
conformance suite unless it is an explicitly reviewed framework boundary.

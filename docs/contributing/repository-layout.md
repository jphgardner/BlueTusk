# Repository and solution layout

BlueTusk is one monorepo with independently versioned Provider, Streams, Sync,
Live, Control Plane, and Continuous Graph product families. Physical
directories answer where a file lives; solution folders answer which product
and role it belongs to.

## Physical directories

| Directory | Purpose |
| --- | --- |
| `src` | Runtime packages for every product family |
| `tests` | Unit, conformance, integration, stress, and compatibility suites |
| `extensions` | PostgreSQL extension packages and EF adapters |
| `identity` | Cloud identity integrations |
| `clients` | TypeScript and framework clients |
| `tooling` | CLI and inspection tools |
| `samples` | Executable product examples |
| `templates` | Packaged project templates |
| `benchmarks` | BenchmarkDotNet projects and checked-in baselines |
| `eng` | CI, release, verification, Compose, and maintenance tooling |
| `docs` | Architecture, operations, compatibility, and release evidence |

The root `BlueTusk.slnx` contains all 114 buildable repository projects. Its
folders are deliberately product-oriented:

- `Provider` contains the wire stack, ADO.NET, EF Core, replication,
  extensibility, extension packages, identity, tools, samples, templates, and
  their focused tests.
- `Streams`, `Sync`, and `Live` each separate core, integrations, storage or
  destinations, testing packages, transports, samples, tools, and tests.
- `Operations` contains the Control Plane and Dashboard.
- `ContinuousGraph` contains its runtime, samples, and tests.
- `Tests` contains genuinely cross-product integration and stress suites.
- `Benchmarks` contains the cross-product performance harness.

The two sample projects under `templates/BlueTusk.Extension/content` are
template payloads, not repository build projects, and are intentionally absent
from the root solution.

## Solution integrity

Run:

```powershell
./eng/verify-solution-layout.ps1
```

The validator rejects missing, duplicate, nonexistent, escaping, unsorted, or
misclassified project entries and generic `/src/` or `/tests/` solution
folders. It also rejects project-local `Nullable=enable` or
`ImplicitUsings=enable` declarations already inherited from
`Directory.Build.props`, and requires package descriptions on packable runtime,
extension, identity, and tooling projects. CI runs it before restore. When
adding a project, add it once to the matching product/role folder and keep
folders and paths sorted.

## Generated-output cleanup

Use the repository cleaner instead of broad recursive deletion:

```powershell
# Preview the normal cleanup.
./eng/clean.ps1 -WhatIf

# Remove ignored bin, obj, TestResults, coverage, client dist, and transient
# benchmark report files.
./eng/clean.ps1

# Also remove ignored release/test artifacts and npm dependencies.
./eng/clean.ps1 -IncludeArtifacts -IncludeDependencies
```

Every selected path must resolve below the repository and be ignored by Git;
the script refuses anything tracked or outside that boundary. `artifacts` is
preserved by default because it can contain release-endurance evidence.
`node_modules` is preserved by default to avoid unnecessary package restore.
Personal `.idea`, `.vs`, `.vscode`, and solution user settings are preserved
unless `-IncludeUserSettings` is explicitly supplied.

The cleaner never stops containers, deletes databases, changes Git state, or
removes the global NuGet/npm caches.

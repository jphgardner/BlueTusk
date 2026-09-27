# Work on BlueTusk

Start with the product you are changing. The contributor command checks your
tools, runs a focused test set, and keeps each run's build and test results in
its own directory under `artifacts/dev`.

## Set up your tools

Install Git, PowerShell 7, the .NET SDK selected by `global.json`, and a Node.js
version supported by the repository's Angular packages. Run this from the
repository root:

```powershell
./eng/dev.ps1
dotnet restore BlueTusk.slnx
npm ci
npm ci --prefix website
```

The doctor reports missing tools and whether database configuration exists. It
does not print credentials, install software, or change infrastructure.

## Make a change and test it

```powershell
# Run the Live tests, including recovery and browser-transport contracts.
./eng/dev.ps1 -Task Test -Family Live

# Narrow a run while developing a change.
./eng/dev.ps1 -Task Test -Family Live -Filter 'FullyQualifiedName~LiveQuerySessionTests'

# Check documentation, repository layout, API budgets, and focused tests.
./eng/dev.ps1 -Task Check -Family Live
```

Choose `Provider`, `Streams`, `Sync`, `Live`, `ControlPlane`, or
`ContinuousGraph`. These are focused contributor sets. The Provider set covers
the wire stack and ADO.NET; it does not replace EF specifications, extensions,
integration, stress, or release gates. Use the
[testing guide](testing.md) for those broader checks.
PostgreSQL Sync destination tests live in `BlueTusk.Sync.Tests`; dashboard tests
live in `BlueTusk.ControlPlane.Tests`. The doctor verifies the registered project
paths so a renamed or missing suite cannot disappear silently.

A test run must produce a nonempty TRX result to count as successful. The final
summary records passed and skipped cases, TRX hashes, the HEAD commit and whether
the worktree is clean. Each run uses a fresh SDK artifacts
directory so another build's outputs cannot silently substitute for it.

## Validate against PostgreSQL

The [testing guide](testing.md) lists isolated Compose fixtures and the required
server capabilities. Use a disposable test database: these tests create and
remove their own schemas and some suites create databases.

Set `BLUETUSK_TEST_CONNECTION_STRING` using your local secret mechanism, then
run:

```powershell
./eng/dev.ps1 -Task Test -Family Live -RequireDatabase
```

This mode refuses missing database configuration and skipped tests. The usual
mode allows database-dependent cases to skip and reports that fact explicitly.
Do not interpret an offline run as database compatibility evidence.

## Work on clients and documentation

```powershell
# Build all five browser clients and run the available client tests.
./eng/dev.ps1 -Task Clients

# Validate links and regenerate/check the Angular documentation.
./eng/dev.ps1 -Task Docs

# Generate guides and create a verified production website build.
./eng/dev.ps1 -Task Website

# Start the Angular development server.
npm start --prefix website
```

Edit guides under `docs`; the Angular website generates its guide content from
those sources. Check the generated changes into the same review as the source
guide. A website build does not deploy it.

## Review and hand off

Include the observed problem, the resulting behavior, and the relevant test
results in your review. Keep examples on one exact BlueTusk package version.
Explain ownership, cancellation, durability, and security boundaries where the
change affects them. Consult the [audit action record](../improvement-audit.md)
for outstanding product improvements and their acceptance evidence.
